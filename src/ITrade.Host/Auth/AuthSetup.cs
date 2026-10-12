using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace ITrade.Host.Auth;

/// <summary>
/// Sign-in for the single owner (P2.4.02): cookie session (HttpOnly, Secure, SameSite=Strict), everything
/// requires sign-in unless marked anonymous, and every changing request needs the antiforgery header.
/// </summary>
internal static class AuthSetup
{
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    public const string StampClaim = "itrade:stamp";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public static void AddOwnerAuth(this IServiceCollection services)
    {
        services.AddScoped<OwnerAccounts>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = "itrade.session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            // Twelve hours from sign-in and not a minute more. No sliding renewal: an open tab polls the server,
            // which would otherwise keep a session alive for ever.
            cookie.ExpireTimeSpan = SessionLifetime;
            cookie.SlidingExpiration = false;
            // An API answers with a status, not with a redirect to a login page.
            cookie.Events.OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden);
            cookie.Events.OnValidatePrincipal = RejectSessionsOfAnOlderPassword;
        });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = AntiforgeryHeader;
            antiforgery.Cookie.Name = "itrade.xsrf";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });
    }

    /// <summary>Authentication, authorization, then the antiforgery check of every changing request.</summary>
    public static void UseOwnerAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
                || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
            if (!safe)
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    await Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "The antiforgery token is missing or not valid.").ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });
    }

    public static void MapOwnerAuth(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/api/auth").WithTags("Auth");

        // Also hands out the antiforgery token; it is bound to the user, so the client asks again after sign-in.
        auth.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return new SessionResponse(
                context.User.Identity?.IsAuthenticated == true, tokens.RequestToken!, AntiforgeryHeader);
        }).AllowAnonymous().WithName("GetSession");

        auth.MapPost("/login", async (
            LoginRequest request, HttpContext context, OwnerAccounts accounts, IClock clock, CancellationToken ct) =>
        {
            var result = await accounts.LoginAsync(request.Password, ct);
            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    var identity = new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, OwnerAccounts.Owner), new Claim(StampClaim, result.Stamp.ToString())],
                        CookieAuthenticationDefaults.AuthenticationScheme);
                    await context.SignInAsync(new ClaimsPrincipal(identity));
                    return Results.NoContent();
                case LoginOutcome.Locked:
                    var wait = (result.LockedUntil!.Value - clock.GetCurrentInstant()).TotalSeconds;
                    context.Response.Headers.RetryAfter =
                        Math.Max(1, (long)Math.Ceiling(wait)).ToString(CultureInfo.InvariantCulture);
                    return Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many failed sign-in attempts. Try again later.");
                default:
                    return Results.Problem(
                        statusCode: StatusCodes.Status401Unauthorized, title: "The password is not correct.");
            }
        })
            .AllowAnonymous()
            .WithName("Login")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", async (HttpContext context, OwnerAccounts accounts, CancellationToken ct) =>
        {
            await accounts.EndAllSessionsAsync(ct);
            await context.SignOutAsync();
            return Results.NoContent();
        })
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task RejectSessionsOfAnOlderPassword(CookieValidatePrincipalContext context)
    {
        var accounts = context.HttpContext.RequestServices.GetRequiredService<OwnerAccounts>();
        var account = await accounts.FindAsync(context.HttpContext.RequestAborted);
        var stamp = context.Principal?.FindFirstValue(StampClaim);
        if (account is null || stamp != account.Stamp.ToString())
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    }

    private static Task Status(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}

internal sealed record LoginRequest(string Password);

internal sealed record SessionResponse(bool Authenticated, string AntiforgeryToken, string AntiforgeryHeader);

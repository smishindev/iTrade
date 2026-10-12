using Microsoft.Extensions.FileProviders;

namespace ITrade.Host.Web;

/// <summary>
/// Serves the built web interface (web/dist, copied to wwwroot next to the Host) from the same origin as the
/// API (P2.5.06). The files are public — the sign-in page has to load without a session; the data is not.
/// </summary>
internal static class WebUi
{
    /// <summary>
    /// Only this origin may supply scripts, data and frames; inline styles are allowed because the component
    /// library (MUI/Emotion) injects them. A script that got into a page could not load code or send data elsewhere.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    private const string IndexFile = "index.html";

    /// <summary>Address prefixes that are never pages of the interface.</summary>
    private static string[] ServerPrefixes { get; } = ["api", "hubs", "openapi"];

    /// <summary>Security headers on every answer, then the static files — before sign-in is checked.</summary>
    public static void UseWebUi(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = file =>
            {
                // Vite puts a content hash into the names under assets/, so those never change.
                var hashed = file.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase);
                file.Context.Response.Headers.CacheControl =
                    hashed ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
    }

    /// <summary>
    /// Every address that is not a file and not a server address is a screen of the interface: the browser gets
    /// index.html and the router inside it shows the screen (so a reload or a bookmark of /taxes works).
    /// </summary>
    public static void MapWebUi(this WebApplication app)
    {
        foreach (var prefix in ServerPrefixes)
        {
            // Behind sign-in like the rest of the API: 401 without a session, 404 with one — never a page.
            app.MapFallback(
                $"/{prefix}/{{**rest}}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
        }

        app.MapFallback("{*path:nonfile}", (HttpContext context, IWebHostEnvironment environment) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            }

            var index = environment.WebRootFileProvider.GetFileInfo(IndexFile);
            if (!index.Exists)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The web interface is not built. Run `npm run build` in web/, then build the Host.");
            }

            context.Response.Headers.CacheControl = "no-cache";
            return Results.Stream(index.CreateReadStream(), "text/html; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();
    }
}

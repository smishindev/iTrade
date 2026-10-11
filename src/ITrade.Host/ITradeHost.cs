using System.Globalization;
using System.Net;
using ITrade.Host.Auth;
using ITrade.Host.Events;
using ITrade.Host.Health;
using ITrade.Host.Logging;
using ITrade.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodaTime;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;

namespace ITrade.Host;

/// <summary>
/// Builds and starts the web application (PLAN §3.1): HTTPS on 127.0.0.1 only, JSON logs without secrets,
/// migrations applied at start, PAPER mode. Tests start this same host on a free port.
/// </summary>
public static partial class ITradeHost
{
    public const string TestingEnvironment = "Testing";
    public const string ConnectionStringName = "ITrade";
    public const string DataDirVariable = "ITRADE_DATA_DIR";

    /// <summary>Version of the HTTP API and name of its OpenAPI document; the web client is generated from it.</summary>
    public const string ApiVersion = "v1";

    private const int LogRetentionDays = 90;
    private const long LogFileSizeLimitBytes = 50 * 1024 * 1024;

    /// <param name="args">Command-line settings.</param>
    /// <param name="configureServices">Last word on the services (tests replace the clock).</param>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        AddConfigurationSources(builder);
        var options = ITradeOptions.Read(builder.Configuration);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not set. Store it outside the repository: " +
                $"dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"Host=127.0.0.1;Port=55432;" +
                "Database=itrade;Username=itrade;Password=…\" --project src/ITrade.Host");
        }

        // Explicit endpoints replace any "urls" setting, so nothing but the loopback address is ever bound.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, options.Port, listen => listen.UseHttps()));

        var masker = SecretMasker.For(builder.Configuration);
        builder.Services.AddSerilog(
            (_, logger) => ConfigureLogging(logger, options, masker), preserveStaticLogger: true);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddITradeDatabase(connectionString);
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        builder.Services.AddOwnerAuth();
        builder.Services.AddEvents();

        // Every error leaves as RFC 9457 problem details, in every environment (P2.4.03).
        builder.Services.AddProblemDetails();
        builder.Services.Configure<RouteHandlerOptions>(routes => routes.ThrowOnBadRequest = false);
        builder.Services.AddOpenApi(ApiVersion, openApi => openApi.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "iTrade API";
            document.Info.Version = ApiVersion;
            return Task.CompletedTask;
        }));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        // The logger is this host's own (no static Log.Logger), so the request log needs it handed over.
        app.UseSerilogRequestLogging(
            requests => requests.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSameOriginForHubs();
        app.UseOwnerAuth();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealth }).AllowAnonymous();
        app.MapOwnerAuth();
        app.MapEvents();
        app.MapOpenApi(); // /openapi/v1.json — behind sign-in, like everything not marked anonymous
        return app;
    }

    /// <summary>Applies pending migrations, then starts listening.</summary>
    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        await MigrateAsync(app, cancellationToken);
        await app.StartAsync(cancellationToken);
        var options = app.Services.GetRequiredService<ITradeOptions>();
        LogStarted(app.Logger, options.Mode, app.Urls, options.LogDir);
    }

    public static async Task MigrateAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITradeDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "iTrade started in {Mode} mode on {Urls}; logs in {LogDir}")]
    private static partial void LogStarted(
        Microsoft.Extensions.Logging.ILogger logger, TradingMode mode, IEnumerable<string> urls, string logDir);

    /// <summary>
    /// Adds, below environment variables and the command line: the mode file (appsettings.Paper.json),
    /// user-secrets in every environment but tests (a test must never reach the owner's database), and
    /// the data folder variable shared with the Python tools.
    /// </summary>
    private static void AddConfigurationSources(WebApplicationBuilder builder)
    {
        var mode = ITradeOptions.ParseMode(builder.Configuration[$"{ITradeOptions.Section}:Mode"]);
        var added = new List<IConfigurationSource>
        {
            new JsonConfigurationSource { Path = $"appsettings.{mode}.json", Optional = false },
        };
        if (!builder.Environment.IsEnvironment(TestingEnvironment))
        {
            added.AddRange(
                new ConfigurationBuilder().AddUserSecrets(typeof(AssemblyMarker).Assembly, optional: true).Sources);
        }

        if (Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } dataDir)
        {
            added.Add(new MemoryConfigurationSource
            {
                InitialData = [new($"{ITradeOptions.Section}:DataDir", dataDir)],
            });
        }

        var sources = builder.Configuration.Sources;
        var at = sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource);
        foreach (var source in added)
        {
            sources.Insert(at < 0 ? sources.Count : at++, source);
        }
    }

    private static void ConfigureLogging(LoggerConfiguration logger, ITradeOptions options, SecretMasker masker)
    {
        var console = new MessageTemplateTextFormatter(
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", CultureInfo.InvariantCulture);
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Mode", options.Mode.ToString())
            .WriteTo.Console(new MaskingFormatter(console, masker))
            .WriteTo.File(
                new MaskingFormatter(new CompactJsonFormatter(), masker),
                Path.Combine(options.LogDir, "itrade-.json"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(LogRetentionDays));
    }

    private static Task WriteHealth(HttpContext context, HealthReport report)
    {
        var options = context.RequestServices.GetRequiredService<ITradeOptions>();
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            mode = options.Mode.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        });
    }
}

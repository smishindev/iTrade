using System.Globalization;
using ITrade.Application.Fx;
using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Fx;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ITrade.Host.Fx;

internal static class FxSetup
{
    private const long LargestAnswerBytes = 8 * 1024 * 1024; // the whole history since 2005 is under 0.5 MB

    public static void AddFxRates(this IServiceCollection services, ITradeOptions options)
    {
        services.AddScoped<IFxRateStore, FxRateStore>();
        services.AddScoped<FxRates>();
        // The only place the platform calls out to the internet so far: a public page of the Bank of Israel,
        // read without a key. Nothing about the owner is sent.
        services.AddHttpClient<IFxRateSource, BankOfIsraelRates>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = LargestAnswerBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("iTrade/1.0");
        });
        if (options.UpdateFxRates)
        {
            services.AddScoped<IDailyJob, FxRatesJob>();
        }
    }
}

/// <summary>
/// `ITrade.Host update-fx-rates [--accept-changes CODE "why"]`: brings the exchange rates up to today right now —
/// the whole history the first time. It is what the daily job does; running it again adds nothing. When the bank
/// states another rate for a stored date, or a new rate moves unusually far, nothing is stored and the command
/// says so, with a code for exactly that finding. After looking at the bank's own page the owner runs it again
/// with `--accept-changes`, the code and the reason; if the bank says anything else by then, nothing is accepted.
/// </summary>
internal static class UpdateFxRatesCommand
{
    public const string Name = "update-fx-rates";
    public const string AcceptChanges = "--accept-changes";
    private const string Placeholder = "<what you checked>";

    /// <summary>
    /// Splits the command's own arguments from the settings of the Host. After `--accept-changes` come the code
    /// and the reason; whatever is missing is left empty, and <see cref="RunAsync"/> then explains the usage.
    /// </summary>
    public static (FxAcceptance? Accepted, string[] HostArgs) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is not [AcceptChanges, .. var rest])
        {
            return (null, args);
        }

        // Exactly two words are the command's: the code and the reason. A third one means the reason was typed
        // without its quotes - taking only its first word would record half a sentence in a row that stays.
        var own = rest.TakeWhile(a => !a.StartsWith('-')).ToArray();
        return own.Length == 2
            ? (new FxAcceptance(own[0], own[1]), rest[2..])
            : (new FxAcceptance("", ""), rest[own.Length..]);
    }

    /// <param name="app">A built, not started host (database and settings).</param>
    /// <param name="accepted">The owner's acceptance of a finding shown earlier; null for a plain update.</param>
    /// <param name="output">Where the summary goes.</param>
    public static async Task<int> RunAsync(WebApplication app, FxAcceptance? accepted, TextWriter output)
    {
        if (accepted is not null && (string.IsNullOrWhiteSpace(accepted.Code) || string.IsNullOrWhiteSpace(accepted.Reason)
            || accepted.Reason.Trim().Length > FxRateStore.MaxReasonLength || accepted.Reason.Contains(Placeholder, StringComparison.Ordinal)))
        {
            await output.WriteLineAsync(
                $"Usage: {Name} {AcceptChanges} <code> \"{Placeholder}\" - the code is shown with the finding; " +
                $"the reason, in your own words and in quotes (up to {FxRateStore.MaxReasonLength} characters), is recorded with the rate.");
            return 2;
        }

        await ITradeHost.MigrateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var rates = scope.ServiceProvider.GetRequiredService<FxRates>();
        var source = scope.ServiceProvider.GetRequiredService<IFxRateSource>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        FxRatesUpdate update;
        try
        {
            update = await rates.UpdateAsync(accepted, CancellationToken.None);
        }
        catch (FxRatesDisagreeException e)
        {
            await output.WriteLineAsync($"The exchange rates were not updated: {e.Message}");
            await output.WriteLineAsync(
                $"To accept exactly this: dotnet run --project src/ITrade.Host -- {Name} {AcceptChanges} {e.Code} \"{Placeholder}\"");
            return 1;
        }
        // What else can go wrong with a source on the internet: it is away, or says something unexpected; and
        // the Host's own job may have stored the same rates a moment ago.
        catch (Exception e) when (e is TemporaryFailureException or FormatException or InvalidOperationException or DbUpdateException)
        {
            var cause = e.InnerException is null ? e.Message : $"{e.Message} ({e.InnerException.Message})";
            await output.WriteLineAsync($"The exchange rates were not updated: {cause}");
            return 1;
        }

        // The update has written itself down in the audit trail, together with the rates.
        var today = clock.GetCurrentInstant().InZone(Zones.Israel).Date;
        var latest = await rates.OnAsync(update.Latest, CancellationToken.None);
        var pair = $"{source.BaseCurrency}/{source.QuoteCurrency}";
        await output.WriteLineAsync(Invariant(
            $"{pair} ({source.Name}): {update.Added} new; {update.Compared} of the last {FxRates.OverlapDays} days read again and unchanged."));
        await output.WriteLineAsync(Invariant(
            $"Latest: {latest.Rate.Rate:0.######} for {update.Latest:R}{(update.Latest < today ? $" (no rate for today, {today:R}, yet)" : "")}."));
        foreach (var change in update.Revised)
        {
            await output.WriteLineAsync(Invariant(
                $"Revised {change.Stored.Date:R}: {change.Stored.Rate:0.######} -> {change.Stated.Rate:0.######} (the old rate is kept as the earlier revision)."));
        }

        foreach (var rate in update.Unusual)
        {
            await output.WriteLineAsync(Invariant($"Accepted an unusual move: {rate.Rate:0.######} for {rate.Date:R}."));
        }

        foreach (var rate in update.Late)
        {
            await output.WriteLineAsync(Invariant(
                $"Late: {rate.Rate:0.######} for {rate.Date:R} was published after later days; until now that date used a carried rate."));
        }

        foreach (var rate in update.NotRepeated)
        {
            await output.WriteLineAsync(Invariant(
                $"Note: the source did not repeat the stored rate of {rate.Date:R} ({rate.Rate:0.######}); it is kept as it is."));
        }

        return 0;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

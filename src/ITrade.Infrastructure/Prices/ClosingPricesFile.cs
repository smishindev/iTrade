using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ITrade.Application.Prices;
using ITrade.Domain;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Infrastructure.Prices;

/// <summary>
/// Reads the export of closing prices the data pipeline writes (`itrade export-closes`, python/src/itrade/data/closes.py):
/// `closing-prices.csv` with the lines `ticker,session,close`, the exchange's sessions those cover in
/// `closing-prices-sessions.csv`, and `closing-prices.json` with the SHA-256 of both and what is known about
/// each ticker. The tables are taken only if they are exactly the ones the manifest vouches for, and anything
/// that is not as the pipeline writes it stops the whole export — with its line number where there is one.
/// </summary>
public static class ClosingPricesFile
{
    public const string CsvName = "closing-prices.csv";
    public const string ManifestName = "closing-prices.json";
    public const string Header = "ticker,session,close";
    public const string SessionsHeader = "session";

    /// <summary>Where the pipeline puts the export inside the data folder.</summary>
    public static string DefaultPath(string dataDir) => Path.Combine(dataDir, "derived", "closing-prices", CsvName);

    /// <param name="csvPath">
    /// The table of prices; its manifest is the `.json` of the same name beside it, and the manifest names the
    /// table of sessions, in the same folder.
    /// </param>
    public static ClosingPricesExport Read(string csvPath)
    {
        var manifestPath = Path.ChangeExtension(csvPath, ".json");
        if (!File.Exists(csvPath) || !File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"There is no export of closing prices at {csvPath} (with its manifest). " +
                "Write it with: uv run --directory python itrade export-closes");
        }

        JsonDocument manifest;
        try
        {
            manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        }
        catch (JsonException e)
        {
            throw new FormatException($"The manifest of the closing prices is not JSON: {e.Message}", e);
        }

        using (manifest)
        {
            var sessionsName = manifest.RootElement.ValueKind == JsonValueKind.Object
                && manifest.RootElement.TryGetProperty("sessions_file", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()!
                    : throw new FormatException(
                        "The manifest of the closing prices names no table of sessions: it was written by an older exporter. Export again.");
            // A name, not a path: the manifest does not get to point anywhere else.
            if (sessionsName.Length == 0 || sessionsName != Path.GetFileName(sessionsName))
            {
                throw new FormatException($"The manifest's 'sessions_file' is not a file name: '{sessionsName}'.");
            }

            var sessionsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(csvPath))!, sessionsName);
            if (!File.Exists(sessionsPath))
            {
                throw new FileNotFoundException($"The export's table of sessions is not there: {sessionsPath}. Export again.");
            }

            return Read(File.ReadAllBytes(csvPath), File.ReadAllBytes(sessionsPath), manifest.RootElement);
        }
    }

    public static ClosingPricesExport Read(byte[] table, byte[] sessionsTable, JsonElement manifest)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sessionsTable);
        try
        {
            return ReadChecked(table, sessionsTable, manifest);
        }
        // A manifest whose values are of another kind than expected or that names a ticker twice, or a table
        // that is not text.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormatException($"The export of closing prices is not as the pipeline writes it: {e.Message}", e);
        }
    }

    private static ClosingPricesExport ReadChecked(byte[] table, byte[] sessionsTable, JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The manifest of the closing prices is not a JSON object.");
        }

        var sha256 = Vouched(table, Text(manifest, "sha256"), "closing prices");
        Vouched(sessionsTable, Text(manifest, "sessions_sha256"), "sessions");
        var exportedAt = Time(Text(manifest, "exported_at"), "'exported_at'");

        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var sessions = ReadSessions(utf8.GetString(sessionsTable));
        if (!manifest.TryGetProperty("sessions", out var sessionCount) || !sessionCount.TryGetInt32(out var statedSessions)
            || statedSessions != sessions.Count)
        {
            throw new FormatException($"The manifest counts another number of sessions than their table has ({sessions.Count}).");
        }

        var lines = ReadLines(utf8.GetString(table));
        if (!manifest.TryGetProperty("rows", out var rows) || !rows.TryGetInt32(out var count) || count != lines.Count)
        {
            throw new FormatException($"The manifest counts another number of rows than the table has ({lines.Count}).");
        }

        var tickers = ReadTickers(manifest);
        var rowsOf = lines.GroupBy(l => l.Ticker, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (ticker, stated) in tickers)
        {
            if (rowsOf.GetValueOrDefault(ticker) != stated.Rows)
            {
                throw new FormatException(
                    $"The manifest counts {stated.Rows} row(s) of {ticker}; the table has {rowsOf.GetValueOrDefault(ticker)}.");
            }
        }

        if (rowsOf.Keys.FirstOrDefault(t => !tickers.ContainsKey(t)) is { } unnamed)
        {
            throw new FormatException($"The table holds prices of {unnamed}, which the manifest does not name.");
        }

        return new ClosingPricesExport(
            Text(manifest, "source"), sha256, exportedAt, Text(manifest, "calendar"), sessions,
            tickers.ToDictionary(t => t.Key, t => t.Value.About, StringComparer.OrdinalIgnoreCase), lines);
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"The manifest of the closing prices has no '{name}'.");

    private static Instant Time(string text, string name)
    {
        var time = OffsetDateTimePattern.ExtendedIso.Parse(text);
        return time.Success
            ? time.Value.ToInstant()
            : throw new FormatException($"The manifest's {name} is not a time: '{text}'.");
    }

    /// <summary>The table's SHA-256, when it is the one the manifest states.</summary>
    private static string Vouched(byte[] table, string stated, string what)
    {
        var actual = Convert.ToHexString(SHA256.HashData(table)).ToLowerInvariant();
        return string.Equals(stated, actual, StringComparison.OrdinalIgnoreCase)
            ? actual
            : throw new FormatException(
                $"The table of {what} is not the one its manifest vouches for (SHA-256 {actual}, the manifest says " +
                $"{stated}): the export was caught half-written, or the file was changed. Export again.");
    }

    private static Dictionary<string, (int Rows, ExportedTicker About)> ReadTickers(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("tickers", out var tickers) || tickers.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The manifest of the closing prices has no 'tickers'.");
        }

        var about = new Dictionary<string, (int, ExportedTicker)>(StringComparer.Ordinal);
        foreach (var ticker in tickers.EnumerateObject())
        {
            var stated = ticker.Value;
            if (stated.ValueKind != JsonValueKind.Object || !stated.TryGetProperty("rows", out var rows) || !rows.TryGetInt32(out var count))
            {
                throw new FormatException($"The manifest does not count the rows of {ticker.Name}.");
            }

            string? currency = null;
            if (stated.TryGetProperty("currency", out var code) && code.ValueKind != JsonValueKind.Null)
            {
                currency = code.ValueKind == JsonValueKind.String && code.GetString() is { Length: 3 } text
                    ? text
                    : throw new FormatException($"The manifest's currency of {ticker.Name} is not a currency code.");
            }

            var splitDays = new HashSet<LocalDate>();
            if (!stated.TryGetProperty("splits", out var splits) || splits.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"The manifest does not say what splits {ticker.Name} had.");
            }

            foreach (var split in splits.EnumerateObject())
            {
                var day = LocalDatePattern.Iso.Parse(split.Name);
                splitDays.Add(day.Success
                    ? day.Value
                    : throw new FormatException($"The manifest's split of {ticker.Name} on '{split.Name}' is not on a date."));
            }

            about.Add(ticker.Name, (count, new ExportedTicker(
                Time(Text(stated, "fetched_at"), $"'fetched_at' of {ticker.Name}"), currency, splitDays)));
        }

        return about;
    }

    private static List<LocalDate> ReadSessions(string text)
    {
        var sessions = new List<LocalDate>();
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            if (number == 1)
            {
                if (line != SessionsHeader)
                {
                    throw new FormatException($"Line 1 of the sessions is not the header '{SessionsHeader}'.");
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue; // the end of the file
            }

            var session = LocalDatePattern.Iso.Parse(line);
            if (!session.Success)
            {
                throw new FormatException($"Line {number} of the sessions is not a date: '{line}'.");
            }

            if (sessions.Count > 0 && session.Value <= sessions[^1])
            {
                throw new FormatException($"Line {number} of the sessions: {line} is not after the session before it.");
            }

            sessions.Add(session.Value);
        }

        return sessions;
    }

    private static List<ClosingPriceLine> ReadLines(string text)
    {
        var lines = new List<ClosingPriceLine>();
        var number = 0;
        (string Ticker, LocalDate Session)? previous = null;
        foreach (var line in text.Split('\n'))
        {
            number++;
            if (number == 1)
            {
                if (line != Header)
                {
                    throw new FormatException($"Line 1 is not the header '{Header}'.");
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue; // the end of the file
            }

            var fields = line.Split(',');
            var session = fields.Length == 3 ? LocalDatePattern.Iso.Parse(fields[1]) : null;
            if (session is not { Success: true } || fields[0].Length == 0 || fields[0] != fields[0].Trim()
                || !decimal.TryParse(fields[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var close)
                || close <= 0m || decimal.Round(close, ClosingPrice.Scale) != close)
            {
                throw new FormatException($"Line {number} is not a ticker, a date and a price: '{line}'.");
            }

            // One ticker after another, each in order of date: the same session cannot come twice unnoticed.
            if (previous is { } before && before.Ticker == fields[0] && session.Value <= before.Session)
            {
                throw new FormatException($"Line {number}: the sessions of {fields[0]} are not in order of date.");
            }

            previous = (fields[0], session.Value);
            lines.Add(new ClosingPriceLine(fields[0], session.Value, close));
        }

        return lines;
    }
}

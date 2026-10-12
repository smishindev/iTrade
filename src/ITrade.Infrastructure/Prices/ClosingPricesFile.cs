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
/// `closing-prices-sessions.csv`, the funds' dividends and splits in `closing-prices-events.csv`, and
/// `closing-prices.json` with the SHA-256 of the three and what is known about each ticker. The tables are
/// taken only if they are exactly the ones the manifest vouches for, and anything that is not as the pipeline
/// writes it stops the whole export — with its line number where there is one.
/// </summary>
public static class ClosingPricesFile
{
    public const string CsvName = "closing-prices.csv";
    public const string ManifestName = "closing-prices.json";
    public const string Header = "ticker,session,close";
    public const string SessionsHeader = "session";
    public const string EventsHeader = "ticker,day,kind,value";

    /// <summary>Where the pipeline puts the export inside the data folder.</summary>
    public static string DefaultPath(string dataDir) => Path.Combine(dataDir, "derived", "closing-prices", CsvName);

    /// <param name="csvPath">
    /// The table of prices; its manifest is the `.json` of the same name beside it, and the manifest names the
    /// tables of sessions and of fund events, in the same folder.
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
            var folder = Path.GetDirectoryName(Path.GetFullPath(csvPath))!;
            var sessions = Beside(folder, manifest.RootElement, "sessions_file", "sessions");
            var events = Beside(folder, manifest.RootElement, "events_file", "fund events");
            return Read(File.ReadAllBytes(csvPath), File.ReadAllBytes(sessions), File.ReadAllBytes(events), manifest.RootElement);
        }
    }

    public static ClosingPricesExport Read(byte[] table, byte[] sessionsTable, byte[] eventsTable, JsonElement manifest)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sessionsTable);
        ArgumentNullException.ThrowIfNull(eventsTable);
        try
        {
            return ReadChecked(table, sessionsTable, eventsTable, manifest);
        }
        // A manifest whose values are of another kind than expected or that names a ticker twice, or a table
        // that is not text.
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormatException($"The export of closing prices is not as the pipeline writes it: {e.Message}", e);
        }
    }

    /// <summary>The table the manifest names under <paramref name="key"/>, in the folder of the export.</summary>
    private static string Beside(string folder, JsonElement manifest, string key, string what)
    {
        var fileName = manifest.ValueKind == JsonValueKind.Object
            && manifest.TryGetProperty(key, out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()!
                : throw new FormatException(
                    $"The manifest of the closing prices names no table of {what}: it was written by an older exporter. Export again.");
        // A name, not a path: the manifest does not get to point anywhere else.
        if (fileName.Length == 0 || fileName != Path.GetFileName(fileName))
        {
            throw new FormatException($"The manifest's '{key}' is not a file name: '{fileName}'.");
        }

        var path = Path.Combine(folder, fileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"The export's table of {what} is not there: {path}. Export again.");
    }

    private static ClosingPricesExport ReadChecked(byte[] table, byte[] sessionsTable, byte[] eventsTable, JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The manifest of the closing prices is not a JSON object.");
        }

        var sha256 = Vouched(table, Text(manifest, "sha256"), "closing prices");
        Vouched(sessionsTable, Text(manifest, "sessions_sha256"), "sessions");
        Vouched(eventsTable, Text(manifest, "events_sha256"), "fund events");
        var exportedAt = Time(Text(manifest, "exported_at"), "'exported_at'");

        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var sessions = ReadSessions(utf8.GetString(sessionsTable));
        if (Count(manifest, "sessions") != sessions.Count)
        {
            throw new FormatException($"The manifest counts another number of sessions than their table has ({sessions.Count}).");
        }

        var lines = ReadLines(utf8.GetString(table));
        if (Count(manifest, "rows") != lines.Count)
        {
            throw new FormatException($"The manifest counts another number of rows than the table has ({lines.Count}).");
        }

        var events = ReadEvents(utf8.GetString(eventsTable));
        if (Count(manifest, "events") != events.Count)
        {
            throw new FormatException($"The manifest counts another number of fund events than their table has ({events.Count}).");
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

        if (events.FirstOrDefault(e => !tickers.ContainsKey(e.Ticker)) is { } stray)
        {
            throw new FormatException($"The table of fund events holds one of {stray.Ticker}, which the manifest does not name.");
        }

        return new ClosingPricesExport(
            Text(manifest, "source"), sha256, exportedAt, Text(manifest, "calendar"), sessions,
            tickers.ToDictionary(t => t.Key, t => t.Value.About, StringComparer.OrdinalIgnoreCase), lines, events);
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"The manifest of the closing prices has no '{name}'.");

    /// <summary>A count the manifest states; -1 when it states none (no table has that many lines).</summary>
    private static int Count(JsonElement manifest, string name) =>
        manifest.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : -1;

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

    private static List<FundEventLine> ReadEvents(string text)
    {
        var events = new List<FundEventLine>();
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            if (number == 1)
            {
                if (line != EventsHeader)
                {
                    throw new FormatException($"Line 1 of the fund events is not the header '{EventsHeader}'.");
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue; // the end of the file
            }

            var fields = line.Split(',');
            var day = fields.Length == 4 ? LocalDatePattern.Iso.Parse(fields[1]) : null;
            FundEventKind? kind = fields.Length == 4
                ? fields[2] switch { "dividend" => FundEventKind.Dividend, "split" => FundEventKind.Split, _ => null }
                : null;
            if (day is not { Success: true } || kind is null || fields[0].Length == 0 || fields[0] != fields[0].Trim()
                || !decimal.TryParse(fields[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || value <= 0m || decimal.Round(value, FundEvent.Scale) != value)
            {
                throw new FormatException($"Line {number} of the fund events is not a ticker, a date, a kind and an amount: '{line}'.");
            }

            // One ticker after another, each in order of date, a dividend before a split of the same day:
            // the same event cannot come twice unnoticed.
            if (events.Count > 0 && events[^1] is var before && before.Ticker == fields[0]
                && (day.Value < before.Day || (day.Value == before.Day && kind.Value <= before.Kind)))
            {
                throw new FormatException($"Line {number} of the fund events: the events of {fields[0]} are not in order, each once.");
            }

            events.Add(new FundEventLine(fields[0], day.Value, kind.Value, value));
        }

        return events;
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

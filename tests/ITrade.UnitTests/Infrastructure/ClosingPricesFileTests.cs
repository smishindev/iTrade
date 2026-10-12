using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ITrade.Application.Prices;
using ITrade.Domain;
using ITrade.Infrastructure.Prices;
using NodaTime;

namespace ITrade.UnitTests.Infrastructure;

/// <summary>
/// P3.3.02: reading the export of closing prices the data pipeline writes — taken only when its tables are
/// exactly the ones the manifest vouches for, and refused whole when anything in it is not as the pipeline
/// writes it.
/// </summary>
public sealed class ClosingPricesFileTests
{
    private const string SessionsName = "closing-prices-sessions.csv";
    private const string EventsName = "closing-prices-events.csv";

    /// <summary>
    /// Written by the real exporter (python/src/itrade/data/closes.py) from four small series; the Python test
    /// `test_fixture_the_platform_reads_is_what_the_exporter_writes` holds the two sides together.
    /// </summary>
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string[] Rows(string table) => table.Split('\n').Skip(1).Where(l => l.Length > 0).ToArray();

    private static string Table(params string[] lines) => ClosingPricesFile.Header + "\n" + string.Join('\n', lines) + "\n";

    private static string Sessions(params string[] days) => ClosingPricesFile.SessionsHeader + "\n" + string.Join('\n', days) + "\n";

    private static string Events(params string[] lines) => ClosingPricesFile.EventsHeader + "\n" + string.Join('\n', lines) + (lines.Length > 0 ? "\n" : "");

    private static readonly byte[] NoEvents = Encoding.UTF8.GetBytes(Events());

    /// <summary>The manifest the exporter would write for these tables, as far as the platform reads it.</summary>
    private static JsonObject Manifest(byte[] table, byte[] sessions, byte[]? events = null)
    {
        events ??= NoEvents;
        var tickers = new JsonObject();
        foreach (var rows in Rows(Encoding.Latin1.GetString(table)).GroupBy(l => l.Split(',')[0]))
        {
            tickers[rows.Key] = new JsonObject
            {
                ["rows"] = rows.Count(),
                ["fetched_at"] = "2026-01-07T03:00:00+00:00",
                ["currency"] = "USD",
                ["splits"] = new JsonObject(),
            };
        }

        return new JsonObject
        {
            ["file"] = ClosingPricesFile.CsvName,
            ["sha256"] = Hash(table),
            ["rows"] = Rows(Encoding.Latin1.GetString(table)).Length,
            ["source"] = "yfinance",
            ["calendar"] = "XNYS",
            ["sessions_file"] = SessionsName,
            ["sessions_sha256"] = Hash(sessions),
            ["sessions"] = Rows(Encoding.Latin1.GetString(sessions)).Length,
            ["events_file"] = EventsName,
            ["events_sha256"] = Hash(events),
            ["events"] = Rows(Encoding.Latin1.GetString(events)).Length,
            ["exported_at"] = "2026-01-07T06:00:00+00:00",
            ["tickers"] = tickers,
        };
    }

    /// <summary>Reads the tables with their manifest — after <paramref name="change"/> had its way with it.</summary>
    private static ClosingPricesExport Read(string table, string? sessions = null, Action<JsonObject>? change = null, string? events = null)
    {
        var bytes = Encoding.UTF8.GetBytes(table);
        var days = Encoding.UTF8.GetBytes(sessions ?? Sessions("2026-01-02", "2026-01-05", "2026-01-06"));
        var happened = events is null ? NoEvents : Encoding.UTF8.GetBytes(events);
        var manifest = Manifest(bytes, days, happened);
        change?.Invoke(manifest);
        return ClosingPricesFile.Read(bytes, days, happened, JsonSerializer.SerializeToElement(manifest));
    }

    private static readonly string Vt = Table("VT,2026-01-05,120.13");

    [Fact]
    public void ExportOfTheRealExporterIsReadExactly()
    {
        var export = ClosingPricesFile.Read(Path.Combine(Fixtures, ClosingPricesFile.CsvName));

        Assert.Equal(
            ("yfinance", "ee293aea5c6da4eb77b619a06ce07563f355f5878a2960a7f3e1d7f299becc30", Instant.FromUtc(2026, 1, 7, 6, 0), "XNYS"),
            (export.Source, export.Sha256, export.ExportedAt, export.Calendar));
        Assert.Equal(
            [
                new ClosingPriceLine("QQQ", new LocalDate(2025, 12, 29), 110.8125m), // sixteenths of a dollar, exact
                new ClosingPriceLine("QQQ", new LocalDate(2025, 12, 30), 107.6875m),
                new ClosingPriceLine("VT", new LocalDate(2026, 1, 5), 120.13m),
                new ClosingPriceLine("VT", new LocalDate(2026, 1, 6), 121.50m),
                new ClosingPriceLine("XLC", new LocalDate(2025, 12, 30), 49.532m), // a tenth of a cent, as it was quoted
                new ClosingPriceLine("XLC", new LocalDate(2026, 1, 2), 49.75m),
                new ClosingPriceLine("XLK", new LocalDate(2026, 1, 5), 289.99m), // as traded, not the halved figure of the vendor
                new ClosingPriceLine("XLK", new LocalDate(2026, 1, 6), 146.60m),
            ],
            export.Lines);
        // New Year's Day and the weekend are no sessions; 31 December is one, though XLC has no price for it.
        Assert.Equal(
            [new(2025, 12, 29), new(2025, 12, 30), new(2025, 12, 31), new(2026, 1, 2), new(2026, 1, 5), new LocalDate(2026, 1, 6)],
            export.Sessions);
        Assert.Equal(["QQQ", "VT", "XLC", "XLK"], export.Tickers.Keys.Order(StringComparer.Ordinal));
        Assert.All(export.Tickers.Values, about => Assert.Equal(Instant.FromUtc(2026, 1, 7, 3, 0), about.FetchedAt));
        Assert.Equal(("USD", null), (export.Tickers["VT"].Currency, export.Tickers["QQQ"].Currency)); // QQQ: the pipeline did not know
        Assert.Equal([new LocalDate(2026, 1, 6)], export.Tickers["XLK"].SplitDays);
        Assert.Empty(export.Tickers["VT"].SplitDays);
        Assert.Same(export.Tickers["XLK"], export.Tickers["xlk"]);
        // What the funds did: VT paid 0.5673 a share; XLK paid 0.168 the day before it split 2:1 - the vendor
        // shows that dividend halved, the export tells it as it was paid.
        Assert.Equal(
            [
                new FundEventLine("VT", new LocalDate(2026, 1, 6), FundEventKind.Dividend, 0.5673m),
                new FundEventLine("XLK", new LocalDate(2026, 1, 5), FundEventKind.Dividend, 0.168m),
                new FundEventLine("XLK", new LocalDate(2026, 1, 6), FundEventKind.Split, 2m),
            ],
            export.Events);
    }

    [Fact]
    public void TableOfFundEventsIsDividendsAndSplitsInOrderEachOnce()
    {
        var both = Table("VT,2026-01-05,120.13", "XLK,2026-01-06,146.60");
        string[] bad =
        [
            // Each of these is a day after the event on line 2, so that nothing but the line itself is wrong.
            "VT,2026-01-06,dividend", // no amount
            "VT,2026-01-06,dividend,0.5673,USD",
            ",2026-01-06,dividend,0.5673",
            "VT,06/01/2026,dividend,0.5673",
            "VT,2026-01-06,bonus,0.5673", // a kind the platform does not know
            "VT,2026-01-06,Dividend,0.5673",
            "VT,2026-01-06,dividend,0",
            "VT,2026-01-06,dividend,-0.5673",
            "VT,2026-01-06,dividend,0.56730001", // finer than is kept
            "VT,2026-01-06,dividend,0.5673\r",
            "VT,2026-01-02,split,2", // before the event on line 2: out of order
            "VT,2026-01-05,dividend,0.6", // the same event twice
            "QQQ,2026-01-05,dividend,0.5", // a fund the manifest does not name
        ];

        Assert.All(bad, line => Assert.Throws<FormatException>(() => Read(both, events: Events("VT,2026-01-05,dividend,0.5673", line))));
        Assert.Contains(
            "Line 3 of the fund events",
            Assert.Throws<FormatException>(() => Read(both, events: Events("VT,2026-01-05,dividend,0.5673", bad[0]))).Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Read(both, events: "ticker,day,kind\nVT,2026-01-05,dividend\n"));

        // A dividend and a split on one day, then another fund: that is in order.
        var read = Read(both, events: Events("VT,2026-01-05,dividend,0.5673", "VT,2026-01-05,split,0.25", "XLK,2026-01-02,split,2"));
        Assert.Equal(
            [(FundEventKind.Dividend, 0.5673m), (FundEventKind.Split, 0.25m), (FundEventKind.Split, 2m)],
            read.Events.Select(e => (e.Kind, e.Value)));

        // The table is the one the manifest vouches for, line for line.
        var events = Events("VT,2026-01-05,dividend,0.5673");
        var other = Assert.Throws<FormatException>(() => Read(both, events: events, change: m => m["events_sha256"] = Hash(NoEvents)));
        Assert.Contains("The table of fund events is not the one its manifest vouches for", other.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Read(both, events: events, change: m => m["events"] = 2));
        Assert.Throws<FormatException>(() => Read(both, events: events, change: m => m.Remove("events")));
        Assert.Throws<FormatException>(() => Read(both, events: events, change: m => m.Remove("events_sha256")));
        Assert.Empty(Read(both).Events);
    }

    [Fact]
    public void TablesThatAreNotTheOnesTheManifestVouchesForAreRefused()
    {
        // One digit changed after the manifest was written, or the export caught in the middle.
        var changed = Assert.Throws<FormatException>(() =>
            Read(Table("VT,2026-01-05,120.18"), change: m => m["sha256"] = Hash(Encoding.UTF8.GetBytes(Vt))));
        Assert.Contains("The table of closing prices is not the one its manifest vouches for", changed.Message, StringComparison.Ordinal);
        var otherDays = Assert.Throws<FormatException>(() =>
            Read(Vt, change: m => m["sessions_sha256"] = Hash(Encoding.UTF8.GetBytes(Sessions("2026-01-05")))));
        Assert.Contains("The table of sessions is not the one its manifest vouches for", otherDays.Message, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => Read(Vt, change: m => m["rows"] = 2));
        Assert.Throws<FormatException>(() => Read(Vt, change: m => m["sessions"] = 2));
        Assert.Throws<FormatException>(() => Read(Vt, change: m => m["exported_at"] = "yesterday"));
        Assert.Throws<FormatException>(() => Read(Vt, change: m => m["tickers"]!["VT"]!["rows"] = 2));
        Assert.Throws<FormatException>(() => Read(Vt, change: m => m["tickers"]!["XLK"] = m["tickers"]!["VT"]!.DeepClone())); // rows of nothing
        var unnamed = Assert.Throws<FormatException>(() => Read(Vt, change: m => m["tickers"] = new JsonObject()));
        Assert.Contains("prices of VT, which the manifest does not name", unnamed.Message, StringComparison.Ordinal);
        Assert.Single(Read(Vt).Lines);
    }

    [Fact]
    public void ManifestThatIsNotAsThePipelineWritesItIsRefusedInWords()
    {
        Action<JsonObject>[] broken =
        [
            m => m.Remove("sha256"),
            m => m.Remove("sessions_sha256"),
            m => m.Remove("source"),
            m => m.Remove("calendar"),
            m => m.Remove("exported_at"),
            m => m.Remove("tickers"),
            m => m["sha256"] = 5,
            m => m["rows"] = "1", // a number written as text
            m => m["rows"] = 1.5,
            m => m["sessions"] = "3",
            m => m["tickers"] = new JsonArray(),
            m => m["tickers"]!["VT"] = "USD",
            m => m["tickers"]!["VT"]!.AsObject().Remove("fetched_at"),
            m => m["tickers"]!["VT"]!["fetched_at"] = "2026-01-07",
            m => m["tickers"]!["VT"]!["currency"] = "DOLLARS",
            m => m["tickers"]!["VT"]!["currency"] = 840,
            m => m["tickers"]!["VT"]!.AsObject().Remove("splits"),
            m => m["tickers"]!["VT"]!["splits"] = new JsonObject { ["Monday"] = 2.0 },
            m => m["tickers"]!["vt"] = m["tickers"]!["VT"]!.DeepClone(), // one ticker twice
        ];

        Assert.All(broken, change => Assert.Throws<FormatException>(() => Read(Vt, change: change)));
        Assert.Throws<FormatException>(() =>
            ClosingPricesFile.Read(Encoding.UTF8.GetBytes(Vt), Encoding.UTF8.GetBytes(Sessions("2026-01-05")), NoEvents, JsonSerializer.SerializeToElement("no object")));
        // A currency the pipeline does not know is not a fault; a split is told by its day.
        var read = Read(Vt, change: m =>
        {
            m["tickers"]!["VT"]!["currency"] = null;
            m["tickers"]!["VT"]!["splits"] = new JsonObject { ["2026-01-05"] = 2.0 };
        });
        Assert.Equal((null, new LocalDate(2026, 1, 5)), (read.Tickers["VT"].Currency, read.Tickers["VT"].SplitDays.Single()));
    }

    [Fact]
    public void TableThatIsNotTextIsRefusedInWords()
    {
        byte[] table = [.. Encoding.UTF8.GetBytes(ClosingPricesFile.Header + "\nVT,2026-01-05,120.13\n"), 0xC3, 0x28];
        var days = Encoding.UTF8.GetBytes(Sessions("2026-01-05"));
        var manifest = Manifest(Encoding.UTF8.GetBytes(Vt), days);
        manifest["sha256"] = Hash(table);

        var error = Assert.Throws<FormatException>(() => ClosingPricesFile.Read(table, days, NoEvents, JsonSerializer.SerializeToElement(manifest)));

        Assert.StartsWith("The export of closing prices is not as the pipeline writes it:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LineThatIsNotATickerADateAndAPriceStopsTheWholeFile()
    {
        string[] bad =
        [
            "VT,2026-01-05", // no price
            "VT,2026-01-05,120.13,USD",
            ",2026-01-05,120.13",
            " VT,2026-01-05,120.13",
            "VT,05/01/2026,120.13",
            "VT,2026-02-30,120.13",
            "VT,2026-01-05,",
            "VT,2026-01-05,0",
            "VT,2026-01-05,-120.13",
            "VT,2026-01-05,1.2e2",
            "VT,2026-01-05,120.1300001", // finer than the table keeps: it would be rounded without a word
            "VT,2026-01-05,120.13\r", // a tool rewrote the line ends
        ];

        Assert.All(bad, line =>
        {
            var error = Assert.Throws<FormatException>(() => Read(Table("VT,2026-01-02,119.00", line)));
            Assert.Contains("Line 3", error.Message, StringComparison.Ordinal);
        });
        Assert.Throws<FormatException>(() => Read("ticker;session;close\nVT;2026-01-05;120.13\n"));
        Assert.Equal(120.130000m, Read(Table("VT,2026-01-05,120.130000")).Lines[0].Close);
    }

    [Fact]
    public void SameSessionTwiceOrSessionsOutOfOrderAreRefused()
    {
        Assert.Throws<FormatException>(() => Read(Table("VT,2026-01-05,120.13", "VT,2026-01-05,120.14")));
        Assert.Throws<FormatException>(() => Read(Table("VT,2026-01-06,121.50", "VT,2026-01-05,120.13")));
        // Another ticker starts its own run of dates.
        Assert.Equal(2, Read(Table("VT,2026-01-06,121.50", "XLK,2026-01-05,289.99")).Lines.Count);
    }

    [Fact]
    public void TableOfSessionsIsDatesInOrderEachOnce()
    {
        string[] bad =
        [
            Sessions("2026-01-05", "2026-01-05"),
            Sessions("2026-01-06", "2026-01-05"),
            Sessions("2026-01-05", "6 January"),
            Sessions("2026-01-05", "2026-01-06\r"),
            Sessions("2026-01-05", "2026-01-06,open"),
            "sessions\n2026-01-05\n", // another header
        ];

        Assert.All(bad, sessions => Assert.Throws<FormatException>(() => Read(Vt, sessions)));
        Assert.Contains("Line 3 of the sessions", Assert.Throws<FormatException>(() => Read(Vt, bad[0])).Message, StringComparison.Ordinal);
        Assert.Equal([new(2026, 1, 5), new LocalDate(2026, 1, 6)], Read(Vt, Sessions("2026-01-05", "2026-01-06")).Sessions);
        Assert.Empty(Read(Table(), Sessions()).Sessions); // an export of nothing is still an export
    }

    [Fact]
    public void ManifestNamesItsTableOfSessionsByNameOnly()
    {
        var folder = Directory.CreateTempSubdirectory("itrade-prices-").FullName;
        var path = Path.Combine(folder, ClosingPricesFile.CsvName);
        var table = Encoding.UTF8.GetBytes(Vt);
        var days = Encoding.UTF8.GetBytes(Sessions("2026-01-05"));
        void Write(Action<JsonObject>? change = null)
        {
            var manifest = Manifest(table, days);
            change?.Invoke(manifest);
            File.WriteAllBytes(path, table);
            File.WriteAllText(Path.Combine(folder, ClosingPricesFile.ManifestName), manifest.ToJsonString());
        }

        Write();
        Assert.Contains("table of sessions is not there", Assert.Throws<FileNotFoundException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(folder, SessionsName), days);
        Assert.Contains("table of fund events is not there", Assert.Throws<FileNotFoundException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(folder, EventsName), NoEvents);
        Assert.Single(ClosingPricesFile.Read(path).Sessions);
        Write(m => m["events_file"] = "../itrade-sessions-elsewhere.csv");
        Assert.Contains("'events_file' is not a file name", Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);
        Write(m => m.Remove("events_file"));
        Assert.Contains("names no table of fund events: it was written by an older exporter", Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);

        // The same tables one folder up: the manifest does not get to point there.
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(folder)!, "itrade-sessions-elsewhere.csv"), days);
        Write(m => m["sessions_file"] = "../itrade-sessions-elsewhere.csv");
        Assert.Contains("is not a file name", Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);
        Write(m => m["sessions_file"] = "");
        Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path));

        // What the exporter wrote before it knew about sessions; and a manifest that is no JSON.
        Write(m => m.Remove("sessions_file"));
        Assert.Contains("written by an older exporter", Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, ClosingPricesFile.ManifestName), "{ \"file\": ");
        Assert.Contains("is not JSON", Assert.Throws<FormatException>(() => ClosingPricesFile.Read(path)).Message, StringComparison.Ordinal);

        File.Delete(Path.Combine(Path.GetDirectoryName(folder)!, "itrade-sessions-elsewhere.csv"));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void MissingExportSaysHowToMakeOne()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ClosingPricesFile.CsvName);

        var error = Assert.Throws<FileNotFoundException>(() => ClosingPricesFile.Read(nowhere));

        Assert.Contains("itrade export-closes", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("derived", "closing-prices", "closing-prices.csv"), ClosingPricesFile.DefaultPath(@"D:\ITradeData"), StringComparison.Ordinal);
    }
}

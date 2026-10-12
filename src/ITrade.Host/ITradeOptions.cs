namespace ITrade.Host;

/// <summary>Which broker environment the platform works with. LIVE opens with the pilot phase (docs/GATES.md).</summary>
public enum TradingMode
{
    Paper,
    Live,
}

/// <summary>Settings of the platform itself (section "ITrade" of appsettings.json).</summary>
public sealed class ITradeOptions
{
    public const string Section = "ITrade";

    public TradingMode Mode { get; init; } = TradingMode.Paper;

    /// <summary>HTTPS port on 127.0.0.1; 0 picks a free port (tests).</summary>
    public int Port { get; init; } = 7120;

    /// <summary>Data folder outside the repository (same as `data_dir` in config/paths.toml); logs go to its `logs`.</summary>
    public string DataDir { get; init; } = "";

    /// <summary>How often the events hub tells the browser the server is alive.</summary>
    public int HeartbeatSeconds { get; init; } = 15;

    /// <summary>How often the daily jobs are checked for sessions that became due.</summary>
    public int JobsTickSeconds { get; init; } = 60;

    /// <summary>
    /// Whether the daily job fetches exchange rates from the Bank of Israel. On for the owner; the tests switch
    /// it off — they never reach the network — and give the job a source of their own where they need one.
    /// </summary>
    public bool UpdateFxRates { get; init; } = true;

    /// <summary>The log folder, with the separators of this system whatever the setting used.</summary>
    public string LogDir => Path.GetFullPath(Path.Combine(DataDir, "logs"));

    /// <summary>The mode named in configuration; PAPER when absent. Anything but PAPER is refused for now.</summary>
    public static TradingMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TradingMode.Paper;
        }

        if (!Enum.TryParse<TradingMode>(value, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException($"Unknown {Section}:Mode '{value}'; expected Paper.");
        }

        if (mode != TradingMode.Paper)
        {
            throw new InvalidOperationException(
                $"{Section}:Mode '{mode}' is not available before the pilot phase (docs/GATES.md); only Paper is.");
        }

        return mode;
    }

    public static ITradeOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        ParseMode(section["Mode"]);
        var options = section.Get<ITradeOptions>() ?? new ITradeOptions();
        if (!Path.IsPathFullyQualified(options.DataDir))
        {
            throw new InvalidOperationException(
                $"{Section}:DataDir must be an absolute path (see config/paths.toml), got '{options.DataDir}'.");
        }

        if (options.HeartbeatSeconds <= 0 || options.JobsTickSeconds <= 0)
        {
            throw new InvalidOperationException($"{Section}:HeartbeatSeconds and {Section}:JobsTickSeconds must be positive.");
        }

        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "Kestrel:Endpoints is not supported: the platform listens on 127.0.0.1 only (set ITrade:Port).");
        }

        return options;
    }
}

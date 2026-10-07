using System.Text.Json;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// Settings from appsettings.json, overridden by the git-ignored appsettings.Local.json.
/// The account number never lives in the repo; the spike only checks its prefix.
/// </summary>
public sealed record SpikeConfig
{
    /// <summary>Paper ports only: IB Gateway paper and TWS paper. Live ports are refused.</summary>
    public static readonly IReadOnlySet<int> PaperPorts = new HashSet<int> { 4002, 7497 };

    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 4002;
    public int ClientId { get; init; } = 101;
    public string ExpectedAccountPrefix { get; init; } = "DU";
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public string LogDirectory { get; init; } = "out";

    public static SpikeConfig Load(string baseDirectory)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = new SpikeConfig();
        foreach (var name in new[] { "appsettings.json", "appsettings.Local.json" })
        {
            var path = Path.Combine(baseDirectory, name);
            if (File.Exists(path))
            {
                config = Merge(config, JsonSerializer.Deserialize<PartialConfig>(File.ReadAllText(path), options));
            }
        }

        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (Host is not ("127.0.0.1" or "localhost"))
        {
            throw new InvalidOperationException($"Host must be localhost, got '{Host}'.");
        }

        if (!PaperPorts.Contains(Port))
        {
            throw new InvalidOperationException(
                $"Port {Port} is not a paper-trading port ({string.Join(", ", PaperPorts)}). The spike never connects to live.");
        }

        if (!ExpectedAccountPrefix.StartsWith("DU", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ExpectedAccountPrefix must start with DU (paper accounts).");
        }
    }

    private static SpikeConfig Merge(SpikeConfig current, PartialConfig? overrides) =>
        overrides is null
            ? current
            : current with
            {
                Host = overrides.Host ?? current.Host,
                Port = overrides.Port ?? current.Port,
                ClientId = overrides.ClientId ?? current.ClientId,
                ExpectedAccountPrefix = overrides.ExpectedAccountPrefix ?? current.ExpectedAccountPrefix,
                ConnectTimeoutSeconds = overrides.ConnectTimeoutSeconds ?? current.ConnectTimeoutSeconds,
                LogDirectory = overrides.LogDirectory ?? current.LogDirectory,
            };

    private sealed record PartialConfig(
        string? Host,
        int? Port,
        int? ClientId,
        string? ExpectedAccountPrefix,
        int? ConnectTimeoutSeconds,
        string? LogDirectory);
}

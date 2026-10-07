using System.Text.RegularExpressions;

namespace ITrade.Spikes.Ibkr;

/// <summary>A candidate from config/universes/*.toml: only the fields the spike needs.</summary>
public sealed record UniverseCandidate(string Ticker, string Listing);

/// <summary>
/// Minimal reader for the [[candidates]] tables of a universe TOML file (ticker + listing).
/// Throw-away spike code: a real TOML parser comes with the platform, not here.
/// </summary>
public static partial class Universe
{
    public const string DefaultRelativePath = "config/universes/etf_pullback_v1.toml";

    public static IReadOnlyList<UniverseCandidate> Load(string path)
    {
        var result = new List<UniverseCandidate>();
        string? ticker = null;
        string? listing = null;

        void Flush()
        {
            if (ticker is not null)
            {
                result.Add(new UniverseCandidate(ticker, listing ?? ""));
            }

            ticker = null;
            listing = null;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                continue;
            }

            var match = KeyValue().Match(line);
            if (!match.Success)
            {
                continue;
            }

            switch (match.Groups["key"].Value)
            {
                case "ticker":
                    ticker = match.Groups["value"].Value;
                    break;
                case "listing":
                    listing = match.Groups["value"].Value;
                    break;
            }
        }

        Flush();
        return result;
    }

    /// <summary>Walks up from the current directory to find the repo file.</summary>
    public static string FindDefault()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, DefaultRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{DefaultRelativePath} not found above {Environment.CurrentDirectory}");
    }

    [GeneratedRegex("""^(?<key>[A-Za-z_]+)\s*=\s*"(?<value>[^"]*)"\s*(#.*)?$""")]
    private static partial Regex KeyValue();
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ITrade.Spikes.Ibkr;

/// <summary>
/// Append-only JSON Lines log of every message: one line per callback or request, with a
/// sequence number and UTC timestamp. Lives in out/ (git-ignored); fixtures are anonymised copies.
/// </summary>
public sealed class MessageLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private long _sequence;

    public MessageLog(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"messages-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.jsonl");
        _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
    }

    public string Path { get; }

    public void Write(string direction, string name, IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["seq"] = Interlocked.Increment(ref _sequence),
            ["utc"] = DateTime.UtcNow.ToString("O"),
            ["dir"] = direction,
            ["msg"] = name,
        };
        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        string line;
        try
        {
            line = JsonSerializer.Serialize(payload, Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            payload = payload.ToDictionary(p => p.Key, p => (object?)p.Value?.ToString());
            payload["serializationFallback"] = ex.GetType().Name;
            line = JsonSerializer.Serialize(payload, Options);
        }

        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}

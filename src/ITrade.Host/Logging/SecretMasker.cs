using System.Data.Common;
using System.Text.RegularExpressions;

namespace ITrade.Host.Logging;

/// <summary>
/// Removes secrets from text before it is written to a log. Two layers: the known secret values themselves
/// (connection-string passwords), wherever they appear, and anything that looks like `password=…` / `"token":"…"`.
/// </summary>
public sealed partial class SecretMasker
{
    public const string Mask = "***";

    /// <summary>Shorter values are left to the `key=value` rule: replacing them everywhere would garble the log.</summary>
    private const int MinValueLength = 4;

    private readonly string[] _values;

    public SecretMasker(IEnumerable<string?> secretValues)
    {
        ArgumentNullException.ThrowIfNull(secretValues);
        _values = secretValues
            .Where(v => v is { Length: >= MinValueLength })
            .SelectMany(v => new[] { v!, JsonEscaped(v!) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();
    }

    /// <summary>A masker that knows the passwords of every configured connection string.</summary>
    public static SecretMasker For(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new SecretMasker(configuration.GetSection("ConnectionStrings").GetChildren().Select(c => Password(c.Value)));
    }

    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var value in _values)
        {
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        }

        return KeyValue().Replace(text, "$1$2" + Mask);
    }

    /// <summary>The password of a connection string; the whole string when it cannot be parsed.</summary>
    private static string? Password(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        try
        {
            var parts = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return parts.TryGetValue("Password", out var password) || parts.TryGetValue("Pwd", out password)
                ? password as string
                : null;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    /// <summary>The value as it appears inside a JSON string of the log file.</summary>
    private static string JsonEscaped(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    [GeneratedRegex(
        """(?i)\b(password|passwd|pwd|secret|token|api[_-]?key)(\\?"?\s*[=:]\s*\\?"?)[^\s;,"'\\]+""",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();
}

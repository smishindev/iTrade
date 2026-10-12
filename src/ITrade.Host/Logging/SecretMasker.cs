using System.Data.Common;
using System.Text.RegularExpressions;

namespace ITrade.Host.Logging;

/// <summary>
/// Removes secrets from text before it is written to a log. Two layers: the known secret values themselves
/// (every configured password, token, secret or key), wherever they appear, and anything that looks like
/// `password=…`, `access_token: …` or `"antiforgeryToken":"…"`.
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

    /// <summary>
    /// A masker that knows every secret of the configuration: the passwords inside connection strings and the
    /// value of any setting whose name says it is one (`Flex:Token`, `Broker:ApiKey`, …), wherever it comes from.
    /// </summary>
    public static SecretMasker For(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionStrings = configuration.GetSection("ConnectionStrings").GetChildren()
            .Select(c => Password(c.Value));
        var named = configuration.AsEnumerable()
            .Where(setting => SecretName().IsMatch(setting.Key[(setting.Key.LastIndexOf(':') + 1)..]))
            .Select(setting => setting.Value);
        return new SecretMasker(connectionStrings.Concat(named));
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
            foreach (var name in PasswordKeys)
            {
                if (parts.TryGetValue(name, out var password))
                {
                    return password as string;
                }
            }

            return null;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    /// <summary>The names a connection string may give its password (Npgsql accepts all three).</summary>
    private static string[] PasswordKeys { get; } = ["Password", "Pwd", "Psw"];

    /// <summary>The value as it appears inside a JSON string of the log file.</summary>
    private static string JsonEscaped(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    // The secret word may be the end of a longer name (access_token, POSTGRES_PASSWORD, antiforgeryToken) and
    // may be followed by more of it (passwordHash); then a separator, with or without (escaped) JSON quotes.
    [GeneratedRegex(
        """(?i)((?:password|passwd|pwd|psw|secret|token|api[_-]?key)\w*)(\\?"?\s*[=:]\s*\\?"?)[^\s;,"'\\]+""",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex("(?i)(password|passwd|pwd|psw|secret|token|api[_-]?key)$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}

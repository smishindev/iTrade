using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace ITrade.Host.Logging;

/// <summary>
/// Masks secrets in the final text of every log event — message, properties and exception alike — so nothing
/// reaches a sink unmasked, whatever logged it.
/// </summary>
public sealed class MaskingFormatter(ITextFormatter inner, SecretMasker masker) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        inner.Format(logEvent, buffer);
        output.Write(masker.Apply(buffer.ToString()));
    }
}

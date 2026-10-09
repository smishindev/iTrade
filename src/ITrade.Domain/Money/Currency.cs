namespace ITrade.Domain;

/// <summary>An ISO 4217 currency with the number of minor units used for rounding (USD cents, ILS agorot).</summary>
public readonly record struct Currency
{
    public static readonly Currency Usd = new("USD", 2);
    public static readonly Currency Ils = new("ILS", 2);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    public string Code { get; }

    /// <summary>Digits after the decimal point of the smallest unit (2 for USD and ILS).</summary>
    public int MinorUnits { get; }

    /// <summary>The smallest amount, e.g. 0.01.</summary>
    public decimal MinorUnit => 1m / Pow10(MinorUnits);

    public static Currency Of(string code) => code?.ToUpperInvariant() switch
    {
        "USD" => Usd,
        "ILS" => Ils,
        _ => throw new ArgumentException($"Unsupported currency '{code}'. Add it here with its minor units.", nameof(code)),
    };

    public override string ToString() => Code;

    private static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

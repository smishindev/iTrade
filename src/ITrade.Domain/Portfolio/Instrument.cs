namespace ITrade.Domain;

/// <summary>The symbol an instrument trades under, as the broker writes it: "VOO", "BRK B", "CSPX".</summary>
public readonly record struct Ticker
{
    public const int MaxLength = 12;

    public Ticker(string value)
    {
        var text = value?.Trim().ToUpperInvariant() ?? "";
        var wellFormed = text.Length is > 0 and <= MaxLength
            && char.IsAsciiLetterOrDigit(text[0])
            && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ' ');
        if (!wellFormed)
        {
            throw new ArgumentException(
                $"'{value}' is not a ticker (1 to {MaxLength} letters, digits, '.', '-' or spaces).", nameof(value));
        }

        Value = text;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// An International Securities Identification Number (ISO 6166), e.g. US9229083632. Unlike a ticker it does
/// not change when the fund is renamed or relisted. The last character is a check digit, and it is checked.
/// </summary>
public readonly record struct Isin
{
    public const int Length = 12;

    public Isin(string value)
    {
        var text = value?.Trim().ToUpperInvariant() ?? "";
        if (!IsValid(text))
        {
            throw new ArgumentException(
                $"'{value}' is not an ISIN (two letters, nine letters or digits, and a correct check digit).",
                nameof(value));
        }

        Value = text;
    }

    public string Value { get; }

    /// <summary>The country (or "XS" and the like) that issued the number — not where the fund trades.</summary>
    public string CountryCode => Value[..2];

    /// <summary>True for twelve upper-case characters of the ISIN shape with a correct check digit.</summary>
    public static bool IsValid(string? text)
    {
        if (text is not { Length: Length }
            || !char.IsAsciiLetterUpper(text[0])
            || !char.IsAsciiLetterUpper(text[1])
            || !char.IsAsciiDigit(text[^1]))
        {
            return false;
        }

        // Letters become two digits (A = 10 … Z = 35); then the Luhn rule over all digits, from the right.
        var sum = 0;
        var doubled = false;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                sum += Luhn(c - '0', ref doubled);
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                var number = c - 'A' + 10;
                sum += Luhn(number % 10, ref doubled);
                sum += Luhn(number / 10, ref doubled);
            }
            else
            {
                return false;
            }
        }

        return sum % 10 == 0;
    }

    public override string ToString() => Value;

    /// <summary>Every second digit from the right is doubled, and a two-digit result has its digits added.</summary>
    private static int Luhn(int digit, ref bool doubled)
    {
        var value = doubled ? digit * 2 : digit;
        doubled = !doubled;
        return value > 9 ? value - 9 : value;
    }
}

/// <summary>
/// Something the owner can hold: an ETF or a fund, listed on one exchange in one currency. The identity is
/// <see cref="Id"/>; the ticker may change over time (that history is kept with the accounting data).
/// </summary>
public sealed record Instrument
{
    public const int MaxNameLength = 200;
    public const int MaxExchangeLength = 12;

    public Instrument(
        InstrumentId id, Ticker ticker, string exchange, Currency currency, string name,
        Isin? isin = null, int? ibkrConId = null)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("An instrument needs an id.", nameof(id));
        }

        if (ticker.Value is null)
        {
            throw new ArgumentException("An instrument needs a ticker.", nameof(ticker));
        }

        if (currency.Code is null)
        {
            throw new ArgumentException("An instrument needs a currency.", nameof(currency));
        }

        var market = exchange?.Trim().ToUpperInvariant() ?? "";
        if (market.Length is 0 or > MaxExchangeLength || !market.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"'{exchange}' is not an exchange code (1 to {MaxExchangeLength} letters or digits, e.g. ARCA).",
                nameof(exchange));
        }

        var title = name?.Trim() ?? "";
        if (title.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException($"An instrument needs a name of 1 to {MaxNameLength} characters.", nameof(name));
        }

        if (ibkrConId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ibkrConId), ibkrConId, "An IBKR contract id is positive.");
        }

        Id = id;
        Ticker = ticker;
        Exchange = market;
        Currency = currency;
        Name = title;
        Isin = isin;
        IbkrConId = ibkrConId;
    }

    public InstrumentId Id { get; }

    public Ticker Ticker { get; }

    /// <summary>The primary listing exchange as IBKR names it (ARCA, NASDAQ, LSEETF).</summary>
    public string Exchange { get; }

    /// <summary>The currency the instrument is quoted and traded in.</summary>
    public Currency Currency { get; }

    public string Name { get; }

    public Isin? Isin { get; }

    /// <summary>IBKR's contract id: what an order or a statement refers to; absent for instruments held elsewhere.</summary>
    public int? IbkrConId { get; }
}

using NodaTime;

namespace ITrade.Domain;

/// <summary>A ticker an instrument used to trade under, up to — not including — <see cref="Until"/>.</summary>
public readonly record struct FormerTicker(Ticker Ticker, LocalDate Until);

/// <summary>
/// The tickers of one instrument over time. Funds get renamed (FB became META on 2022-06-09), and a statement
/// or a price series of an earlier date names the ticker of that date. Only renames are recorded: the current
/// ticker holds from the last rename on, each former one up to the date it was replaced.
/// </summary>
public sealed class TickerHistory
{
    private readonly FormerTicker[] _former;

    public TickerHistory(Ticker current, IEnumerable<FormerTicker>? former = null)
    {
        if (current.Value is null)
        {
            throw new ArgumentException("A history needs the current ticker.", nameof(current));
        }

        _former = [.. (former ?? []).OrderBy(f => f.Until)];
        for (var i = 0; i < _former.Length; i++)
        {
            var next = i + 1 < _former.Length ? _former[i + 1].Ticker : current;
            if (_former[i].Ticker == next)
            {
                throw new ArgumentException(
                    $"{_former[i].Ticker} is replaced by itself on {_former[i].Until:R}: that is not a rename.", nameof(former));
            }

            if (i > 0 && _former[i].Until == _former[i - 1].Until)
            {
                throw new ArgumentException($"Two renames on {_former[i].Until:R}.", nameof(former));
            }
        }

        Current = current;
    }

    public Ticker Current { get; }

    /// <summary>Former tickers, oldest first.</summary>
    public IReadOnlyList<FormerTicker> Former => _former;

    /// <summary>The ticker the instrument traded under on a day.</summary>
    public Ticker On(LocalDate day)
    {
        foreach (var former in _former)
        {
            if (day < former.Until)
            {
                return former.Ticker;
            }
        }

        return Current;
    }

    /// <summary>The history after a rename that takes effect on <paramref name="effective"/>.</summary>
    public TickerHistory Renamed(Ticker to, LocalDate effective)
    {
        if (to == Current)
        {
            throw new ArgumentException($"The instrument already trades as {to}.", nameof(to));
        }

        if (_former.Length > 0 && effective <= _former[^1].Until)
        {
            throw new ArgumentException(
                $"A rename on {effective:R} is not after the previous one on {_former[^1].Until:R}.", nameof(effective));
        }

        return new TickerHistory(to, [.. _former, new FormerTicker(Current, effective)]);
    }
}

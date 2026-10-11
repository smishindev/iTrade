using ITrade.Domain;
using NodaTime;

namespace ITrade.Infrastructure.Calendar;

/// <summary>
/// Provisional exchange calendar: Monday to Friday, no holidays. P3 replaces it with the XNYS session
/// calendar exported by the Python pipeline. Until then a daily job may run on an exchange holiday and
/// find nothing to do, and settlement dates ignore holidays — do not use it for money decisions.
/// </summary>
public sealed class WeekdayCalendar : IExchangeCalendar
{
    public bool IsSession(TradeDate day) =>
        day.Value.DayOfWeek is not (IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday);

    public TradeDate NextSession(TradeDate day)
    {
        var next = day.Value.PlusDays(1);
        while (!IsSession(new TradeDate(next)))
        {
            next = next.PlusDays(1);
        }

        return new TradeDate(next);
    }

    public TradeDate SettlementDate(TradeDate tradeDate) => NextSession(tradeDate);
}

using System.Globalization;
using System.Text.Json;
using CsCheck;
using ITrade.Domain;
using NodaTime;
using NodaTime.Text;

namespace ITrade.UnitTests.Domain;

/// <summary>
/// P3.4.02: time-weighted and money-weighted returns of a period — hand-worked examples, the reference
/// calculation in Python (python/src/itrade/accounting/returns.py) on synthetic flows, and what holds for any
/// flows. A contribution counts from the start of its day, a withdrawal at the end of its day.
/// </summary>
public sealed class PeriodReturnsTests
{
    private static LocalDate Day(int year, int month, int day) => new(year, month, day);

    private static ValuePoint At(LocalDate date, decimal value, decimal inflow = 0m, decimal outflow = 0m) => new(date, value, inflow, outflow);

    /// <summary>1,000 paid in on 1 January and grown to 1,100 by the end of March; 1,000 more on 1 April; 2,310 at the end of June.</summary>
    private static readonly ValuePoint[] TwoContributions =
    [
        At(Day(2025, 12, 31), 0m),
        At(Day(2026, 1, 1), 1000m, inflow: 1000m),
        At(Day(2026, 3, 31), 1100m),
        At(Day(2026, 4, 1), 2100m, inflow: 1000m),
        At(Day(2026, 6, 30), 2310m),
    ];

    [Fact]
    public void TimeWeightedReturnDoesNotDependOnWhenTheMoneyCameIn()
    {
        var returns = PeriodReturns.Of(TwoContributions);

        // 10% on the first thousand, then 10% on all of it: 1.1 x 1.1, whatever was paid in between.
        Assert.Equal(0.21m, returns.TimeWeighted);
        Assert.Equal((Day(2025, 12, 31), Day(2026, 6, 30), Day(2025, 12, 31), 181), (returns.Opening, returns.Closing, returns.InvestedSince, returns.DaysAtWork));
        // 2,310 are left of 2,000 paid in: 310 were earned. The contributions are not a gain.
        Assert.Equal((0m, 2310m, 2000m, 0m, 310m), (returns.OpeningValue, returns.ClosingValue, returns.Contributed, returns.Withdrawn, returns.Gain));
        // Half a year is not stretched to a year.
        Assert.Equal((null, null), (returns.TimeWeightedAnnual, returns.MoneyWeightedAnnual));

        // Ten times the second contribution, the same market: the same return.
        var larger = PeriodReturns.Of([.. TwoContributions[..3], At(Day(2026, 4, 1), 11100m, inflow: 10000m), At(Day(2026, 6, 30), 12210m)]);
        Assert.Equal((0.21m, 1210m), (larger.TimeWeighted, larger.Gain)); // 100 on the first thousand, 1,110 on 11,100
    }

    [Fact]
    public void MoneyWeightedReturnIsTheRateThatExplainsWhatTheOwnersMoneyBecame()
    {
        var returns = PeriodReturns.Of(TwoContributions);

        // The first thousand was in for 181 days, the second for 91: at this rate they come to 2,310 together.
        var daily = Math.Pow(1d + (double)returns.MoneyWeighted!.Value, 1d / 181d);
        Assert.Equal(2310d, (1000d * Math.Pow(daily, 181d)) + (1000d * Math.Pow(daily, 91d)), 1e-5);
        Assert.Equal(0.2096024348m, returns.MoneyWeighted);

        // 1,000 grew 20% in half a year; then 10,000 came in and everything fell 10% in a year. The market made
        // 8% - and most of the owner's money was there only for the fall.
        var beforeAFall = PeriodReturns.Of(
            [At(Day(2024, 12, 31), 1000m), At(Day(2025, 6, 30), 1200m), At(Day(2025, 7, 1), 11200m, inflow: 10000m), At(Day(2026, 6, 30), 10080m)]);
        Assert.Equal((0.08m, -920m, 546), (beforeAFall.TimeWeighted, beforeAFall.Gain, beforeAFall.DaysAtWork));
        Assert.Equal(-0.0802392113m, beforeAFall.MoneyWeightedAnnual);
        var perYear = 1d + (double)beforeAFall.MoneyWeightedAnnual!.Value;
        Assert.Equal(10080d, (1000d * Math.Pow(perYear, 546d / 365d)) + (10000d * perYear), 1e-4);
        Assert.True(beforeAFall.MoneyWeighted < 0m && beforeAFall.TimeWeightedAnnual > 0m);
    }

    [Fact]
    public void WithdrawalIsNotALossAndCountsToTheEndOfItsDay()
    {
        // 5,000 had become 6,000.50 on the day 800 were taken out; the 5,200.50 left grew 6%.
        var returns = PeriodReturns.Of([At(Day(2024, 12, 31), 5000m), At(Day(2025, 9, 15), 5200.50m, outflow: 800m), At(Day(2026, 3, 31), 5512.53m)]);

        Assert.Equal(6000.50m / 5000m * (5512.53m / 5200.50m) - 1m, returns.TimeWeighted);
        Assert.Equal(0.272106m, returns.TimeWeighted);
        Assert.Equal((800m, 1312.53m), (returns.Withdrawn, returns.Gain));
        Assert.Equal(455, returns.DaysAtWork);
        Assert.Equal(decimal.Round((decimal)(Math.Pow(1.272106d, 365d / 455d) - 1d), 10), returns.TimeWeightedAnnual);
    }

    [Fact]
    public void PeriodsWithoutMoneyOrWithAllOfItLostHaveAnAnswer()
    {
        var never = PeriodReturns.Of([At(Day(2025, 12, 31), 0m), At(Day(2026, 6, 30), 0m)]);
        Assert.Equal((0m, null, null, 0, 0m, 0m), (never.TimeWeighted, never.MoneyWeighted, never.InvestedSince, never.DaysAtWork, never.Gain, never.GainedOnNothing));

        var lost = PeriodReturns.Of([At(Day(2025, 12, 31), 0m), At(Day(2026, 1, 1), 1000m, inflow: 1000m), At(Day(2026, 2, 1), 0m)]);
        Assert.Equal((-1m, -1m, -1000m), (lost.TimeWeighted, lost.MoneyWeighted, lost.Gain));

        // Empty until March: the return is counted from the end of the day before the money came.
        var later = PeriodReturns.Of(
            [At(Day(2025, 12, 31), 0m), At(Day(2026, 2, 28), 0m), At(Day(2026, 3, 1), 3000m, inflow: 3000m), At(Day(2026, 3, 31), 2940.75m)]);
        Assert.Equal((Day(2026, 2, 28), 31, -0.01975m, -0.01975m), (later.InvestedSince, later.DaysAtWork, later.TimeWeighted, later.MoneyWeighted));

        // Everything taken out at a gain of 5%, three empty months, then 1,500 that grow 2%.
        var again = PeriodReturns.Of(
            [
                At(Day(2025, 12, 31), 2000m), At(Day(2026, 1, 31), 0m, outflow: 2100m), At(Day(2026, 4, 30), 0m),
                At(Day(2026, 5, 1), 1500m, inflow: 1500m), At(Day(2026, 6, 30), 1530m),
            ]);
        Assert.Equal((0.071m, 130m), (again.TimeWeighted, again.Gain)); // 1.05 x 1.02
        // The money was in for 31 + 61 days of the 181. One rate does explain the flows, but carried across
        // the empty months it would say 17% where 130 were made on 2,000: no figure for the period is given.
        Assert.Equal((Day(2025, 12, 31), 92, null, null, 0m), (again.InvestedSince, again.DaysAtWork, again.MoneyWeighted, again.MoneyWeightedAnnual, again.GainedOnNothing));
    }

    [Fact]
    public void YearIsCountedInDaysAtWorkAndTheRatePerYearIsWhileInvested()
    {
        // 1,000 make 10% in nine months and are taken out; a quarter empty; 1,000 again make 5% in nine months.
        var returns = PeriodReturns.Of(
            [
                At(Day(2024, 6, 30), 1000m), At(Day(2025, 3, 31), 0m, outflow: 1100m), At(Day(2025, 6, 30), 0m),
                At(Day(2025, 7, 1), 1000m, inflow: 1000m), At(Day(2026, 3, 31), 1050m),
            ]);

        Assert.Equal((548, 0.155m, 150m), (returns.DaysAtWork, returns.TimeWeighted, returns.Gain)); // 274 + 274 days; 1.1 x 1.05
        Assert.Equal(decimal.Round((decimal)(Math.Pow(1.155d, 365d / 548d) - 1d), 10), returns.TimeWeightedAnnual);
        // Per year while invested: about 10% - and nothing for the period as a whole.
        Assert.Equal((null, 0.1028m), (returns.MoneyWeighted, decimal.Round(returns.MoneyWeightedAnnual!.Value, 4)));
    }

    [Fact]
    public void MoneyThatTurnsUpWhileNothingIsInvestedIsAGainAndInNoReturn()
    {
        // 4,000 taken out at 4,100 on 31 January; on 10 February a dividend of 3.20 still arrives and stays.
        var late = PeriodReturns.Of(
            [At(Day(2025, 12, 31), 4000m), At(Day(2026, 1, 31), 0m, outflow: 4100m), At(Day(2026, 2, 10), 3.20m), At(Day(2026, 6, 30), 3.20m)]);

        Assert.Equal((0.025m, 103.20m, 3.20m), (late.TimeWeighted, late.Gain, late.GainedOnNothing)); // 2.5% on the 4,000; the 3.20 on nothing
        Assert.Null(late.MoneyWeighted);

        // The same when it is taken out on the day it came, and when it comes before any money was ever in.
        var gone = PeriodReturns.Of([At(Day(2025, 12, 31), 0m), At(Day(2026, 1, 31), 0m, outflow: 50m)]);
        Assert.Equal((50m, 50m, 0m, null, 0), (gone.Gain, gone.GainedOnNothing, gone.TimeWeighted, gone.InvestedSince, gone.DaysAtWork));
        var early = PeriodReturns.Of([At(Day(2025, 12, 31), 0m), At(Day(2026, 1, 31), 50m), At(Day(2026, 3, 31), 55m)]);
        Assert.Equal((55m, 50m, 0.1m, Day(2026, 1, 31), 59), (early.Gain, early.GainedOnNothing, early.TimeWeighted, early.InvestedSince, early.DaysAtWork));
        Assert.Null(early.MoneyWeighted); // nothing was ever paid in: there is no money to weigh by
    }

    [Fact]
    public void OnlyDepositsAndWithdrawalsAreTheOwnersMoney()
    {
        var (ibkr, fund) = (HoldingPlaceId.New(), InstrumentId.New());
        var recorded = Instant.FromUtc(2026, 7, 1, 6, 0);
        Transaction Entry(TransactionKind kind, LocalDate day, Money cash, InstrumentId? instrument = null, decimal shares = 0m, TransactionId? relatedTo = null, TransactionId? reverses = null) =>
            new(
                TransactionId.New(), ibkr, kind, day, day, cash, TransactionSource.Manual, recorded, instrument, shares,
                kind == TransactionKind.Buy ? new Price(-cash.Amount / shares, cash.Currency) : null, relatedTo: relatedTo, reverses: reverses);

        // 1,000 paid in on 2 January and 10 shares bought at 90.00. Then, inside the portfolio: a dividend of
        // 10.00 with 2.50 withheld, a fee of 1.00, 0.30 of interest, 0.20 of cash in lieu, and 50 dollars
        // exchanged for 154 shekels. A deposit recorded by mistake and reversed. 200 taken out in June.
        var buy = Entry(TransactionKind.Buy, Day(2026, 1, 2), Money.Usd(-900m), fund, 10m);
        var dividend = Entry(TransactionKind.Dividend, Day(2026, 3, 16), Money.Usd(10m), fund);
        var sold = Entry(TransactionKind.FxConversion, Day(2026, 4, 1), Money.Usd(-50m));
        var mistaken = Entry(TransactionKind.Deposit, Day(2026, 5, 4), Money.Usd(700m));
        Transaction[] ledger =
        [
            Entry(TransactionKind.Deposit, Day(2026, 1, 2), Money.Usd(1000m)),
            buy,
            dividend,
            Entry(TransactionKind.TaxWithheld, Day(2026, 3, 16), Money.Usd(-2.50m), fund, relatedTo: dividend.Id),
            Entry(TransactionKind.Fee, Day(2026, 3, 20), Money.Usd(-1.00m)),
            Entry(TransactionKind.Interest, Day(2026, 3, 31), Money.Usd(0.30m)),
            Entry(TransactionKind.CorporateAction, Day(2026, 3, 31), Money.Usd(0.20m), fund),
            sold,
            Entry(TransactionKind.FxConversion, Day(2026, 4, 1), Money.Ils(154m), relatedTo: sold.Id),
            mistaken,
            Entry(TransactionKind.Deposit, Day(2026, 5, 4), Money.Usd(-700m), reverses: mistaken.Id),
            Entry(TransactionKind.Withdrawal, Day(2026, 6, 15), Money.Usd(-200m)),
        ];

        var flows = OwnerFlow.Of(ledger, Day(2026, 1, 1), Day(2026, 6, 30));

        Assert.Equal(
            [new OwnerFlow(Day(2026, 1, 2), ibkr, Money.Usd(1000m)), new OwnerFlow(Day(2026, 6, 15), ibkr, Money.Usd(-200m))],
            flows);
        Assert.Equal([Money.Usd(-200m)], OwnerFlow.Of(ledger, Day(2026, 1, 3), Day(2026, 6, 15)).Select(f => f.Amount)); // the dates are both included
        Assert.Empty(OwnerFlow.Of(ledger, Day(2026, 1, 3), Day(2026, 6, 14)));

        // With the fund still at 90.00 at the end of March, what is in the dollars of the books: 1,000 paid in,
        // 7.00 earned - the dividend after tax, the interest and the cash in lieu, less the fee. A dividend is
        // a gain, not a contribution.
        decimal Dollars(LocalDate day) =>
            PortfolioBooks.From(ledger, day).CashOf(ibkr, Currency.Usd).Amount + (PortfolioBooks.From(ledger, day).QuantityOf(ibkr, fund).Value * 90m);
        var quarter = PeriodReturns.Of(
            [At(Day(2026, 1, 1), Dollars(Day(2026, 1, 1))), At(Day(2026, 1, 2), Dollars(Day(2026, 1, 2)), inflow: 1000m), At(Day(2026, 3, 31), Dollars(Day(2026, 3, 31)))]);
        Assert.Equal((1000m, 1007m, 7m, 0.007m), (quarter.Contributed, quarter.ClosingValue, quarter.Gain, quarter.TimeWeighted));
    }

    [Fact]
    public void ReturnPerYearIsGivenFromAYearOn()
    {
        // 10,000 on the last day of 2024 are 11,000 a year later, to the day: per year is the same 10%.
        var year = PeriodReturns.Of([At(Day(2024, 12, 31), 10000m), At(Day(2025, 12, 31), 11000m)]);
        Assert.Equal((365, 0.1m, 0.1m, 0.1m, 0.1m), (year.DaysAtWork, year.TimeWeighted, year.TimeWeightedAnnual, year.MoneyWeighted, year.MoneyWeightedAnnual));

        // A day short of a year: only what the period itself made.
        var almost = PeriodReturns.Of([At(Day(2024, 12, 31), 10000m), At(Day(2025, 12, 30), 11000m)]);
        Assert.Equal((364, 0.1m, null, 0.1m, null), (almost.DaysAtWork, almost.TimeWeighted, almost.TimeWeightedAnnual, almost.MoneyWeighted, almost.MoneyWeightedAnnual));

        // A hundredfold in two days is still a return, however unlikely: 1,477.19 became 184,457.42.
        var wild = PeriodReturns.Of([At(Day(2019, 12, 31), 0m), At(Day(2020, 1, 1), 1477.19m, inflow: 1477.19m), At(Day(2020, 1, 2), 184457.42m)]);
        Assert.Equal(decimal.Round((184457.42m / 1477.19m) - 1m, 6), decimal.Round(wild.MoneyWeighted!.Value, 6));

        // Two years at 21% in all are 10% a year.
        var two = PeriodReturns.Of([At(Day(2023, 12, 31), 10000m), At(Day(2025, 12, 30), 12100m)]);
        Assert.Equal((730, 0.21m, 0.1m, 0.1m), (two.DaysAtWork, two.TimeWeighted, two.TimeWeightedAnnual, two.MoneyWeightedAnnual));
    }

    [Fact]
    public void WhatCannotBeAPeriodIsRefused()
    {
        var opening = At(Day(2025, 12, 31), 1000m);
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([At(Day(2025, 12, 31), 1000m, inflow: 1000m), At(Day(2026, 1, 31), 1010m)])); // a flow before the period
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([At(Day(2025, 12, 31), 1000m, outflow: 50m), At(Day(2026, 1, 31), 1010m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 1, 31), 1010m), At(Day(2026, 1, 31), 1020m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 1, 31), 1010m), At(Day(2026, 1, 30), 1020m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 1, 31), -1m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([At(Day(2025, 12, 31), -1m), At(Day(2026, 1, 31), 10m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 1, 31), 1010m, inflow: -5m)]));
        Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 1, 31), 1010m, outflow: -5m)]));

        // Money came in on 1 February, and the value of 31 January is not there to add it to: the rise of
        // January would be credited to the new money.
        var gap = Assert.Throws<ArgumentException>(() => PeriodReturns.Of([opening, At(Day(2026, 2, 1), 2100m, inflow: 1000m)]));
        Assert.Contains("Money came in on 2026-02-01: the value of the day before it is needed", gap.Message, StringComparison.Ordinal);
        Assert.Equal(0.1m, PeriodReturns.Of([opening, At(Day(2026, 1, 31), 1100m), At(Day(2026, 2, 1), 2100m, inflow: 1000m)]).TimeWeighted);
    }

    [Fact]
    public void ReferenceCalculationInPythonAgreesOnSyntheticFlows()
    {
        // Written by the oracle (python/tests/test_returns_oracle.py holds the file to it): fractions for the
        // time-weighted return, the annual-rate equation for the money-weighted one.
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "returns-oracle.json")));
        static decimal Number(JsonElement text) => decimal.Parse(text.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
        static decimal? NumberOrNone(JsonElement text) => text.ValueKind == JsonValueKind.Null ? null : Number(text);
        static void Close(decimal? expected, decimal? actual, decimal tolerance, string what)
        {
            Assert.True(
                (expected is null && actual is null) || (expected is not null && actual is not null && Math.Abs(expected.Value - actual.Value) <= tolerance),
                $"{what}: the oracle says {expected}, the platform {actual}");
        }

        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(23, cases.Count);
        foreach (var example in cases)
        {
            var name = example.GetProperty("name").GetString()!;
            var points = example.GetProperty("points").EnumerateArray().Select(p => new ValuePoint(
                LocalDatePattern.Iso.Parse(p.GetProperty("date").GetString()!).Value,
                Number(p.GetProperty("value")), Number(p.GetProperty("inflow")), Number(p.GetProperty("outflow")))).ToList();
            var expected = example.GetProperty("expected");

            var returns = PeriodReturns.Of(points);

            Close(Number(expected.GetProperty("time_weighted")), returns.TimeWeighted, 1e-18m, $"{name}, time-weighted");
            Close(NumberOrNone(expected.GetProperty("time_weighted_annual")), returns.TimeWeightedAnnual, 1e-9m, $"{name}, time-weighted per year");
            Close(NumberOrNone(expected.GetProperty("money_weighted")), returns.MoneyWeighted, 1e-9m, $"{name}, money-weighted");
            Close(NumberOrNone(expected.GetProperty("money_weighted_annual")), returns.MoneyWeightedAnnual, 1e-9m, $"{name}, money-weighted per year");
            Assert.Equal(Number(expected.GetProperty("gain")), returns.Gain);
            Assert.Equal((expected.GetProperty("days_at_work").GetInt32(), Number(expected.GetProperty("gained_on_nothing"))), (returns.DaysAtWork, returns.GainedOnNothing));
            var since = expected.GetProperty("invested_since");
            Assert.Equal(since.ValueKind == JsonValueKind.Null ? null : LocalDatePattern.Iso.Parse(since.GetString()!).Value, returns.InvestedSince);
        }
    }

    [Fact]
    public void WithOneContributionBothReturnsAreTheSame()
    {
        var gen =
            from paid in Gen.Int[100, 5_000_000]
            from grown in Gen.Int[1, 20_000_000]
            from days in Gen.Int[1, 2000]
            select (Paid: paid / 100m, Grown: grown / 100m, Days: days);

        gen.Sample(x =>
        {
            var start = Day(2020, 1, 1);
            var returns = PeriodReturns.Of([At(start.PlusDays(-1), 0m), At(start, x.Paid, inflow: x.Paid), At(start.PlusDays(x.Days), x.Grown)]);
            var simple = (x.Grown / x.Paid) - 1m;
            return returns.TimeWeighted == simple
                && Math.Abs(returns.MoneyWeighted!.Value - simple) <= 1e-7m * (1m + Math.Abs(simple))
                && returns.Gain == x.Grown - x.Paid
                && (returns.TimeWeightedAnnual is null) == (x.Days + 1 < 365);
        });
    }

    [Fact]
    public void ReturnDoesNotChangeWhenAPeriodIsCutInTwoOrADayIsAdded()
    {
        var gen =
            from opening in Gen.Int[1_000, 1_000_000]
            from middle in Gen.Int[1_000, 1_000_000]
            from paid in Gen.Int[0, 1_000_000]
            from closing in Gen.Int[1_000, 3_000_000]
            select (Opening: opening / 100m, Middle: middle / 100m, Paid: paid / 100m, Closing: closing / 100m);

        gen.Sample(x =>
        {
            var (a, b, c, d) = (Day(2025, 12, 31), Day(2026, 3, 31), Day(2026, 4, 1), Day(2026, 9, 30));
            var whole = PeriodReturns.Of([At(a, x.Opening), At(b, x.Middle), At(c, x.Middle + x.Paid, inflow: x.Paid), At(d, x.Closing)]);
            var first = PeriodReturns.Of([At(a, x.Opening), At(b, x.Middle)]);
            var second = PeriodReturns.Of([At(b, x.Middle), At(c, x.Middle + x.Paid, inflow: x.Paid), At(d, x.Closing)]);
            // One more valued day in between, on which nothing came or went, changes nothing.
            var finer = PeriodReturns.Of([At(a, x.Opening), At(Day(2026, 2, 14), x.Opening + 17m), At(b, x.Middle), At(c, x.Middle + x.Paid, inflow: x.Paid), At(d, x.Closing)]);
            var linked = ((1m + first.TimeWeighted) * (1m + second.TimeWeighted)) - 1m;
            return Math.Abs(whole.TimeWeighted - linked) < 1e-20m
                && Math.Abs(whole.TimeWeighted - finer.TimeWeighted) < 1e-20m
                && whole.MoneyWeighted == finer.MoneyWeighted
                && whole.Gain == first.Gain + second.Gain;
        });
    }
}

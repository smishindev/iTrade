using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// Where a tax figure was read (docs/tax-sources.md): the rule (R1…), the first-hand source, the day it was
/// read, and whether an accountant has checked it - none has (owner, 2026-10-12), so every result built on it is
/// an estimate and has to say so. <paramref name="Rule"/> is null for an open question, which has no rule yet.
/// </summary>
public sealed record TaxSource(string? Rule, string Citation, LocalDate Read, bool CheckedByAccountant);

/// <summary>
/// A tax figure with its source - or an open question (O1…, docs/tax-sources.md) that has no figure: reading
/// its value refuses, so a case without a first-hand rule is never computed with a zero or a guess.
/// </summary>
public sealed class TaxParameter<T>
    where T : notnull
{
    private readonly T? _value;

    public TaxParameter(string name, T value, TaxSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Rule) || string.IsNullOrWhiteSpace(source.Citation))
        {
            throw new ArgumentException($"The tax parameter {name} has a value and no rule or source.", nameof(source));
        }

        (Name, _value, Source) = (name, value, source);
    }

    /// <summary>A parameter whose rule has no first-hand source yet: <paramref name="openQuestion"/> is O1….</summary>
    public TaxParameter(string name, TaxSource source, string openQuestion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(openQuestion);
        (Name, OpenQuestion, Source) = (name, openQuestion, source);
    }

    public string Name { get; }

    public TaxSource Source { get; }

    /// <summary>The open question (O1…) this parameter waits for; null when it has a value.</summary>
    public string? OpenQuestion { get; }

    public bool IsOpen => OpenQuestion is not null;

    /// <summary>The figure; an open question has none and refuses (<see cref="TaxRuleOpenException"/>).</summary>
    public T Value => IsOpen ? throw new TaxRuleOpenException(Name, OpenQuestion!) : _value!;

    public override string ToString() =>
        IsOpen ? $"{Name}: open ({OpenQuestion})" : $"{Name} = {_value} ({Source.Rule}; {Source.Citation})";
}

/// <summary>A case the tax rules have no first-hand source for: it is not computed.</summary>
public sealed class TaxRuleOpenException : InvalidOperationException
{
    public TaxRuleOpenException()
    {
    }

    public TaxRuleOpenException(string message)
        : base(message)
    {
    }

    public TaxRuleOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TaxRuleOpenException(string parameter, string openQuestion)
        : base($"{parameter} has no first-hand source yet (open question {openQuestion}, docs/tax-sources.md): this case is not computed.")
    {
    }
}

/// <summary>The rate a dollar amount becomes shekels with (R3).</summary>
public enum TaxExchangeRate
{
    /// <summary>The Bank of Israel's representative rate of the day, or the last one published before it.</summary>
    BankOfIsraelRepresentative,
}

/// <summary>Which day's rate converts the cost and the proceeds of a trade (R3).</summary>
public enum TradeRateDay
{
    /// <summary>The day of the trade or of the payment, whichever is earlier.</summary>
    EarlierOfTradeAndPayment,
}

/// <summary>Which day's rate converts a dividend or interest (R3).</summary>
public enum IncomeRateDay
{
    /// <summary>The day it was paid or received.</summary>
    DayPaid,
}

/// <summary>What a loss carried to later years may be offset against (R5).</summary>
public enum LossCarryforward
{
    /// <summary>Capital gains only - not dividends or interest.</summary>
    CapitalGainsOnly,
}

/// <summary>
/// The Israeli tax rules of an individual resident, not a new immigrant, investing through a foreign broker, as
/// figures with their sources (config/tax.toml, docs/tax-sources.md). Estimates, not checked by an accountant.
/// </summary>
public sealed record TaxParameters(
    TaxParameter<decimal> CapitalGainsRate,
    TaxParameter<decimal> DividendRate,
    TaxParameter<decimal> UsDividendWithholding,
    TaxParameter<int> ForeignTaxCreditCarryforwardYears,
    TaxParameter<decimal> InterestRate,
    TaxParameter<decimal> SurtaxRate,
    TaxParameter<decimal> SurtaxCapitalRate,
    TaxParameter<decimal> SurtaxThresholdIls,
    TaxParameter<LotMatching> LotMatching,
    TaxParameter<TaxExchangeRate> ExchangeRate,
    TaxParameter<TradeRateDay> ExchangeRateDayOfATrade,
    TaxParameter<IncomeRateDay> ExchangeRateDayOfIncome,
    TaxParameter<bool> LossOffsetAgainstDividendsAndInterest,
    TaxParameter<bool> LossAbroadFirst,
    TaxParameter<LossCarryforward> LossCarryforward,
    TaxParameter<bool> LossCarryforwardNeedsAReturn,
    TaxParameter<IReadOnlyList<AnnualDate>> HalfYearReportDue,
    TaxParameter<AnnualDate> ForeignIncomeAdvanceDue,
    TaxParameter<AnnualDate> AnnualReturnDue,
    TaxParameter<decimal> ExchangeDifferencesOnCash,
    TaxParameter<decimal> UsFundDistributionKinds)
{
    /// <summary>Every parameter by its name: what a result lists as the assumptions it rests on.</summary>
    public IReadOnlyList<(string Name, TaxSource Source, string? OpenQuestion)> Listed =>
    [
        Of(CapitalGainsRate), Of(DividendRate), Of(UsDividendWithholding), Of(ForeignTaxCreditCarryforwardYears),
        Of(InterestRate), Of(SurtaxRate), Of(SurtaxCapitalRate), Of(SurtaxThresholdIls), Of(LotMatching),
        Of(ExchangeRate), Of(ExchangeRateDayOfATrade), Of(ExchangeRateDayOfIncome),
        Of(LossOffsetAgainstDividendsAndInterest), Of(LossAbroadFirst), Of(LossCarryforward),
        Of(LossCarryforwardNeedsAReturn), Of(HalfYearReportDue), Of(ForeignIncomeAdvanceDue), Of(AnnualReturnDue),
        Of(ExchangeDifferencesOnCash), Of(UsFundDistributionKinds),
    ];

    private static (string, TaxSource, string?) Of<T>(TaxParameter<T> parameter)
        where T : notnull => (parameter.Name, parameter.Source, parameter.OpenQuestion);
}

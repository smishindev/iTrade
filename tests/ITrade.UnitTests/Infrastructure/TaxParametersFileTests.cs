using System.Text.RegularExpressions;
using ITrade.Domain;
using ITrade.Host;
using ITrade.Infrastructure.Tax;
using NodaTime;

namespace ITrade.UnitTests.Infrastructure;

/// <summary>
/// P4.1.01: the tax parameters come from config/tax.toml, each with the rule of docs/tax-sources.md and its
/// first-hand source; a parameter without them is refused, and an open question refuses instead of giving a zero.
/// </summary>
public sealed partial class TaxParametersFileTests
{
    private static readonly string FilePath = TaxParametersFile.Locate(AppContext.BaseDirectory);

    private static string Text() => File.ReadAllText(FilePath);

    [Fact]
    public void TheParametersOfTheRepositoryAreReadWithTheirSources()
    {
        var tax = TaxParametersFile.Read(FilePath);

        Assert.Equal(
            (0.25m, 0.25m, 0.25m, 5, 0.25m, 0.03m, 0.02m, 721_560m),
            (tax.CapitalGainsRate.Value, tax.DividendRate.Value, tax.UsDividendWithholding.Value, tax.ForeignTaxCreditCarryforwardYears.Value,
                tax.InterestRate.Value, tax.SurtaxRate.Value, tax.SurtaxCapitalRate.Value, tax.SurtaxThresholdIls.Value));
        Assert.Equal(
            (LotMatching.Fifo, TaxExchangeRate.BankOfIsraelRepresentative, TradeRateDay.EarlierOfTradeAndPayment, IncomeRateDay.DayPaid, LossCarryforward.CapitalGainsOnly),
            (tax.LotMatching.Value, tax.ExchangeRate.Value, tax.ExchangeRateDayOfATrade.Value, tax.ExchangeRateDayOfIncome.Value, tax.LossCarryforward.Value));
        Assert.True(tax.LossOffsetAgainstDividendsAndInterest.Value && tax.LossAbroadFirst.Value && tax.LossCarryforwardNeedsAReturn.Value);
        Assert.Equal([new AnnualDate(7, 31), new AnnualDate(1, 31)], tax.HalfYearReportDue.Value);
        Assert.Equal((new AnnualDate(4, 30), new AnnualDate(4, 30)), (tax.ForeignIncomeAdvanceDue.Value, tax.AnnualReturnDue.Value));
        Assert.Equal(
            ("R1", "Income Tax Ordinance s.91(b)(1) (Nevo, version 2026-09-27); form 1325 of 2025", new LocalDate(2026, 10, 17)),
            (tax.CapitalGainsRate.Source.Rule, tax.CapitalGainsRate.Source.Citation, tax.CapitalGainsRate.Source.Read));

        // Not one of them is checked by an accountant (owner, 2026-10-12): every result is an estimate.
        Assert.Equal(25, tax.Listed.Count);
        Assert.Equal(["lot_matching_abroad", "exchange_differences_on_cash", "us_fund_distribution_kinds", "repurchase_after_a_loss", "fractional_shares"], tax.Listed.Where(p => p.OpenQuestion is not null).Select(p => p.Name));
        Assert.All(tax.Listed, p => Assert.False(p.Source.CheckedByAccountant));
        Assert.Equal((RealGainRule.ExchangeRateAsIndex, Path.GetFullPath(FilePath)), (tax.RealGain.Value, tax.Origin));
    }

    [Fact]
    public void AnOpenQuestionRefusesItsCaseInsteadOfGivingAZero()
    {
        var tax = TaxParametersFile.Read(FilePath);

        var refused = Assert.Throws<TaxRuleOpenException>(() => tax.ExchangeDifferencesOnCash.Value);

        Assert.Equal(
            "exchange_differences_on_cash has no first-hand source yet (open question O3, docs/tax-sources.md): this case is not computed.",
            refused.Message);
        Assert.Equal((true, "O1", (string?)null), (tax.UsFundDistributionKinds.IsOpen, tax.UsFundDistributionKinds.OpenQuestion, tax.UsFundDistributionKinds.Source.Rule));
        Assert.Throws<TaxRuleOpenException>(() => tax.UsFundDistributionKinds.Value);
    }

    [Fact]
    public void EveryRuleAndOpenQuestionOfTheFileIsInTheSources()
    {
        var sources = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(FilePath)!)!, "docs", "tax-sources.md"));
        var tax = TaxParametersFile.Read(FilePath);

        foreach (var (name, source, open) in tax.Listed)
        {
            var named = open is null ? $"### {source.Rule}." : $"| {open} |";
            Assert.True(sources.Contains(named, StringComparison.Ordinal), $"{name}: {named} is not in docs/tax-sources.md");
        }
    }

    [Fact]
    public void TheHostFollowsTheIndexWithTheUsWithholdingOfTheFile()
    {
        // The share of a US fund's dividend that does not reach the owner (W-8BEN on file): one assumption, here and there.
        Assert.Equal(TaxParametersFile.Read(FilePath).UsDividendWithholding.Value, new ITradeOptions().DividendWithheld);
    }

    [Theory]
    [InlineData("source = \"Income Tax Ordinance s.91(b)(1) (Nevo, version 2026-09-27); form 1325 of 2025\"\n", "", "[capital_gains_rate]: no source")]
    [InlineData("source = \"Income Tax Ordinance s.91(b)(1) (Nevo, version 2026-09-27); form 1325 of 2025\"", "source = \"  \"", "[capital_gains_rate]: no source")]
    [InlineData("value = 0.25\nrule = \"R1\"\n", "value = 0.25\n", "[capital_gains_rate]: no 'rule'")]
    [InlineData("rule = \"R1\"", "rule = \"general knowledge\"", "[capital_gains_rate]: no 'rule'")]
    [InlineData("value = 0.25\nrule = \"R1\"", "value = 1.0\nrule = \"R1\"", "[capital_gains_rate]: 1 is not a rate")]
    [InlineData("value = 0.25\nrule = \"R1\"", "value = 0.2500001\nrule = \"R1\"", "[capital_gains_rate]: 0.2500001 is not a rate")]
    [InlineData("value = 0.25\nrule = \"R1\"", "value = \"0.25\"\nrule = \"R1\"", "[capital_gains_rate]: 0.25 is not a number")]
    [InlineData("value = \"fifo\"", "value = \"average\"", "[lot_matching]: 'average' is none of fifo")]
    [InlineData("value = [\"07-31\", \"01-31\"]", "value = [\"07-31\", \"13-40\"]", "[half_year_report_due]: '13-40' is not a day")]
    [InlineData("open = \"O3\"", "open = \"O3\"\nvalue = 0", "[exchange_differences_on_cash]: an open question")]
    [InlineData("open = \"O1\"", "open = \"maybe\"", "[us_fund_distribution_kinds]: an open question")]
    [InlineData("value = 5\n", "value = 5\nnote = \"x\"\n", "[foreign_tax_credit_carryforward_years]: 'note' is not a key")]
    [InlineData("[annual_return_due]", "[annual_return_date]", "has no [annual_return_due]")]
    [InlineData("checked_by_accountant = false\n\n[dividend_rate]", "checked_by_accountant = \"no\"\n\n[dividend_rate]", "[capital_gains_rate]: no 'checked_by_accountant'")]
    [InlineData("read = 2026-10-17\nchecked_by_accountant = false\n\n[dividend_rate]", "read = \"2026-10-17\"\nchecked_by_accountant = false\n\n[dividend_rate]", "[capital_gains_rate]: no 'read' date")]
    public void AParameterWithoutItsSourceOrOutsideWhatItCanBeIsRefused(string written, string instead, string said)
    {
        var text = NewLines().Replace(Text(), "\n");
        Assert.Contains(written, text, StringComparison.Ordinal);
        var changed = ReplaceFirst(text, written, instead);

        var refused = Assert.Throws<FormatException>(() => TaxParametersFile.Parse(changed));

        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AParameterThePlatformDoesNotKnowAndAFileThatIsNoTomlAreRefused()
    {
        var unknown = Text() + "\n[wealth_tax]\nvalue = 0.01\nrule = \"R1\"\nsource = \"x\"\nread = 2026-10-17\nchecked_by_accountant = false\n";

        Assert.Contains("has [wealth_tax], which is no tax parameter", Assert.Throws<FormatException>(() => TaxParametersFile.Parse(unknown)).Message, StringComparison.Ordinal);
        Assert.StartsWith("tax.toml is not valid TOML", Assert.Throws<FormatException>(() => TaxParametersFile.Parse("[a\nb = ")).Message, StringComparison.Ordinal);
    }

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + old.Length));
    }

    [GeneratedRegex("\r\n")]
    private static partial Regex NewLines();
}

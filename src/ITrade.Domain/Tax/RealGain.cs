using NodaTime;

namespace ITrade.Domain;

/// <summary>
/// One part of a sale - one lot's shares - in shekels, for Israeli tax (docs/tax-sources.md R2, R3). Amounts are
/// exact; round them only where they are shown, with a named rule. An estimate, not checked by an accountant.
/// </summary>
/// <param name="Cost">The dollars paid for the shares and their commission, at the purchase rate.</param>
/// <param name="Proceeds">The dollars received less the sale's commission, at the sale rate.</param>
/// <param name="InflationaryAmount">
/// What the cost would have grown by with the exchange rate as the index (s.88): the dollar cost times the change
/// of the rate. Negative when the shekel strengthened - then it counts as nothing.
/// </param>
/// <param name="RealGain">The taxable gain: the nominal gain less the exempt inflationary amount; 0 on a loss.</param>
/// <param name="OffsettableLoss">
/// The loss that may be offset: the whole nominal loss when the shekel weakened; when it strengthened, only the
/// loss in dollars at the sale rate (Moses; circular 10/2025, example 4). 0 on a gain.
/// </param>
public sealed record SaleInShekels(
    Disposal Disposal,
    AppliedFxRate PurchaseRate,
    AppliedFxRate SaleRate,
    decimal Cost,
    decimal Proceeds,
    decimal InflationaryAmount,
    decimal RealGain,
    decimal OffsettableLoss)
{
    /// <summary>Proceeds less cost, in shekels.</summary>
    public decimal NominalGain => Proceeds - Cost;

    /// <summary>The part of a nominal gain that is exempt as inflationary amount.</summary>
    public decimal ExemptInflationaryAmount => NominalGain > 0m ? NominalGain - RealGain : 0m;

    /// <summary>The part of a nominal loss that came from the shekel's strengthening and may not be offset.</summary>
    public decimal LossNotOffsettable => NominalGain < 0m ? -NominalGain - OffsettableLoss : 0m;
}

/// <summary>
/// The real gain or the offsettable loss of a sale of a security bought in dollars, held by an individual
/// (Income Tax Ordinance s.88: for such a security "יראו את שער המטבע כמדד"; circular 10/2025, examples 1-4). The
/// commissions belong to cost and proceeds (form 1325). Rates: the Bank of Israel representative rate of the day of
/// the purchase and of the sale (conversion rules 2003, r.7) - never a provisional one.
/// </summary>
public static class RealGain
{
    /// <summary>The rules this follows, as a result names them.</summary>
    public const string Rules = "R2, R3 (docs/tax-sources.md)";

    /// <summary>
    /// A security bought before this day falls under the transitional rules of 2003-2005 (linear apportionment,
    /// other rates, form 1326): not built, refused.
    /// </summary>
    public static readonly LocalDate FirstPurchaseComputed = new(2006, 1, 1);

    public static SaleInShekels Of(Disposal sale, AppliedFxRate purchaseRate, AppliedFxRate saleRate)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(purchaseRate);
        ArgumentNullException.ThrowIfNull(saleRate);
        var currency = sale.Cost.Currency;
        if (currency != Currency.Usd || sale.PurchaseFees.Currency != currency || sale.Proceeds.Currency != currency || sale.SaleFees.Currency != currency)
        {
            // A security in shekels is indexed by the consumer price index, another currency by its own rate: not built.
            throw new InvalidOperationException($"Only a security bought and sold in dollars is computed; this sale is in {currency}.");
        }

        if (sale.Acquired < FirstPurchaseComputed)
        {
            throw new InvalidOperationException($"The lot was bought on {sale.Acquired:R}, before {FirstPurchaseComputed:R}: the transitional rules for it are not built.");
        }

        foreach (var (rate, day, what) in new[] { (purchaseRate, sale.Acquired, "purchase"), (saleRate, sale.Sold, "sale") })
        {
            if (rate.Rate.From != Currency.Usd || rate.Rate.To != Currency.Ils)
            {
                throw new ArgumentException($"The {what} rate is {rate.Rate.From}/{rate.Rate.To}; a dollar security is converted with USD/ILS.");
            }

            if (rate.Rate.Source != FxSources.BankOfIsraelRepresentative)
            {
                throw new InvalidOperationException($"The {what} rate is of '{rate.Rate.Source}'; Israeli tax converts with the Bank of Israel's representative rate.");
            }

            if (rate.On != day)
            {
                throw new ArgumentException($"The {what} rate is for {rate.On:R}; the {what} was on {day:R}.");
            }

            if (rate.IsProvisional)
            {
                // A tax figure must not move afterwards: a rate the bank may still publish differently is not used.
                throw new InvalidOperationException($"The rate for the {what} on {day:R} is provisional; a tax figure is not computed with it.");
            }
        }

        var (bought, sold) = (purchaseRate.Rate.Rate, saleRate.Rate.Rate);
        var paid = sale.Cost.Amount + sale.PurchaseFees.Amount;
        var received = sale.Proceeds.Amount - sale.SaleFees.Amount;
        var cost = paid * bought;
        var proceeds = received * sold;
        var nominal = proceeds - cost;
        var inflationary = paid * (sold - bought);

        decimal realGain = 0m, offsettableLoss = 0m;
        if (nominal > 0m)
        {
            // A negative inflationary amount counts as nothing; one larger than the gain exempts all of it.
            realGain = nominal - Math.Clamp(inflationary, 0m, nominal);
        }
        else if (nominal < 0m)
        {
            offsettableLoss = sold >= bought ? -nominal : Math.Max(0m, (paid * sold) - proceeds);
        }

        return new SaleInShekels(sale, purchaseRate, saleRate, cost, proceeds, inflationary, realGain, offsettableLoss);
    }
}

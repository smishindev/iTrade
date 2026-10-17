using System.Globalization;
using System.Text.RegularExpressions;
using ITrade.Domain;
using NodaTime;
using NodaTime.Text;
using Tomlyn;
using Tomlyn.Model;

namespace ITrade.Infrastructure.Tax;

/// <summary>
/// Reads config/tax.toml into <see cref="TaxParameters"/>. A parameter is a table with its `value` - or `open`,
/// the open question it waits for - and the `rule` of docs/tax-sources.md it comes from, the first-hand `source`,
/// the day it was `read` and `checked_by_accountant`. Anything else is refused, the whole file: a parameter
/// without a source, a value without a rule, an open question with a value, an unknown key or parameter, a value
/// outside what it can be. A wrong tax figure is worse than none.
/// </summary>
public static partial class TaxParametersFile
{
    /// <summary>Digits after the decimal point a rate keeps; a finer one is refused, not rounded.</summary>
    public const int RateScale = 6;

    private static readonly string[] Keys = ["value", "open", "rule", "source", "read", "checked_by_accountant"];

    private static readonly AnnualDatePattern MonthDay = AnnualDatePattern.CreateWithInvariantCulture("MM'-'dd");

    /// <summary>The file in the repository's config folder, found from the starting folder upwards.</summary>
    public static string Locate(string startDirectory)
    {
        for (var folder = new DirectoryInfo(startDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "config", "tax.toml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"config/tax.toml is not in {startDirectory} or any folder above it.");
    }

    public static TaxParameters Read(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static TaxParameters Parse(string text, string where = "tax.toml")
    {
        TomlTable root;
        try
        {
            root = Toml.ToModel(text);
        }
        catch (TomlException e)
        {
            throw new FormatException($"{where} is not valid TOML: {e.Message}", e);
        }

        var reader = new Reader(root, where);
        var parameters = new TaxParameters(
            reader.Take("capital_gains_rate", Rate),
            reader.Take("dividend_rate", Rate),
            reader.Take("us_dividend_withholding", Rate),
            reader.Take("foreign_tax_credit_carryforward_years", Years),
            reader.Take("interest_rate", Rate),
            reader.Take("surtax_rate", Rate),
            reader.Take("surtax_capital_rate", Rate),
            reader.Take("surtax_threshold_ils", Shekels),
            reader.Take("lot_matching", Named(new Dictionary<string, LotMatching>(StringComparer.Ordinal) { ["fifo"] = LotMatching.Fifo })),
            reader.Take("exchange_rate", Named(new Dictionary<string, TaxExchangeRate>(StringComparer.Ordinal) { ["bank-of-israel-representative"] = TaxExchangeRate.BankOfIsraelRepresentative })),
            reader.Take("exchange_rate_day_of_a_trade", Named(new Dictionary<string, TradeRateDay>(StringComparer.Ordinal) { ["earlier-of-trade-and-payment"] = TradeRateDay.EarlierOfTradeAndPayment })),
            reader.Take("exchange_rate_day_of_income", Named(new Dictionary<string, IncomeRateDay>(StringComparer.Ordinal) { ["day-paid"] = IncomeRateDay.DayPaid })),
            reader.Take("loss_offset_against_dividends_and_interest", Flag),
            reader.Take("loss_abroad_first", Flag),
            reader.Take("loss_carryforward", Named(new Dictionary<string, LossCarryforward>(StringComparer.Ordinal) { ["capital-gains-only"] = LossCarryforward.CapitalGainsOnly })),
            reader.Take("loss_carryforward_needs_a_return", Flag),
            reader.Take("half_year_report_due", Days),
            reader.Take("foreign_income_advance_due", Day),
            reader.Take("annual_return_due", Day),
            reader.Take("exchange_differences_on_cash", Rate),
            reader.Take("us_fund_distribution_kinds", Rate));
        reader.NothingElse();
        return parameters;
    }

    private static decimal Rate(object value)
    {
        var rate = Exact(value);
        return rate is >= 0m and < 1m && decimal.Round(rate, RateScale) == rate
            ? rate
            : throw new FormatException(Invariant($"{rate} is not a rate from 0 to less than 1 with at most {RateScale} decimals"));
    }

    private static decimal Shekels(object value)
    {
        var amount = Exact(value);
        return amount >= 0m && decimal.Round(amount, 2) == amount
            ? amount
            : throw new FormatException(Invariant($"{amount} is not an amount of shekels"));
    }

    private static int Years(object value) =>
        value is long years and > 0 and < 100 ? (int)years : throw new FormatException($"{value} is not a number of years");

    private static bool Flag(object value) => value as bool? ?? throw new FormatException($"{value} is not true or false");

    private static AnnualDate Day(object value) =>
        value is string text && MonthDay.Parse(text) is { Success: true } day
            ? day.Value
            : throw new FormatException($"'{value}' is not a day of the year written MM-dd");

    private static IReadOnlyList<AnnualDate> Days(object value) =>
        value is TomlArray days && days.Count > 0
            ? days.Select(d => Day(d!)).ToList()
            : throw new FormatException($"{value} is not a list of days of the year");

    private static Func<object, T> Named<T>(IReadOnlyDictionary<string, T> names) => value =>
        value is string text && names.TryGetValue(text, out var named)
            ? named
            : throw new FormatException($"'{value}' is none of {string.Join(", ", names.Keys)}");

    /// <summary>
    /// A TOML number as it is written: a float arrives as a double, and its shortest round-trip text is the text
    /// of the file (0.03 → "0.03"), so the decimal is exact.
    /// </summary>
    private static decimal Exact(object value) => value switch
    {
        long whole => whole,
        double real when double.IsFinite(real) => decimal.Parse(real.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"{value} is not a number"),
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^R[0-9]+$")]
    private static partial Regex RuleId();

    [GeneratedRegex("^O[0-9]+$")]
    private static partial Regex OpenId();

    private sealed class Reader(TomlTable root, string where)
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public TaxParameter<T> Take<T>(string name, Func<object, T> convert)
            where T : notnull
        {
            _taken.Add(name);
            if (!root.TryGetValue(name, out var found) || found is not TomlTable table)
            {
                throw new FormatException($"{where} has no [{name}].");
            }

            string Refused(string why) => $"{where}, [{name}]: {why}.";
            foreach (var key in table.Keys.Where(k => !Keys.Contains(k, StringComparer.Ordinal)))
            {
                throw new FormatException(Refused($"'{key}' is not a key of a tax parameter"));
            }

            var source = table.TryGetValue("source", out var s) && s is string text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : throw new FormatException(Refused("no source - a tax figure without its first-hand source is not taken"));
            var read = table.TryGetValue("read", out var r) && r is TomlDateTime { Kind: TomlDateTimeKind.LocalDate } day
                ? LocalDate.FromDateTime(day.DateTime.DateTime)
                : throw new FormatException(Refused("no 'read' date (yyyy-MM-dd) of the source"));
            var checkedByAccountant = table.TryGetValue("checked_by_accountant", out var c) && c is bool isChecked
                ? isChecked
                : throw new FormatException(Refused("no 'checked_by_accountant' (true or false)"));

            if (table.TryGetValue("open", out var open))
            {
                if (open is not string question || !OpenId().IsMatch(question) || table.ContainsKey("value") || table.ContainsKey("rule"))
                {
                    throw new FormatException(Refused("an open question is 'open = \"O…\"' with neither a value nor a rule"));
                }

                return new TaxParameter<T>(name, new TaxSource(null, source, read, checkedByAccountant), question);
            }

            var rule = table.TryGetValue("rule", out var id) && id is string ruleId && RuleId().IsMatch(ruleId)
                ? ruleId
                : throw new FormatException(Refused("no 'rule' (R… of docs/tax-sources.md)"));
            if (!table.TryGetValue("value", out var value) || value is null)
            {
                throw new FormatException(Refused("no value, and not open"));
            }

            T converted;
            try
            {
                converted = convert(value);
            }
            catch (FormatException e)
            {
                throw new FormatException(Refused(e.Message), e);
            }

            return new TaxParameter<T>(name, converted, new TaxSource(rule, source, read, checkedByAccountant));
        }

        public void NothingElse()
        {
            foreach (var name in root.Keys.Where(k => !_taken.Contains(k)).Take(1))
            {
                throw new FormatException($"{where} has [{name}], which is no tax parameter the platform knows.");
            }
        }
    }
}

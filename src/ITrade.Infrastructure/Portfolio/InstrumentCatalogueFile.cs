using System.Globalization;
using System.Text;
using ITrade.Application.Portfolio;
using ITrade.Domain;

namespace ITrade.Infrastructure.Portfolio;

/// <summary>
/// Reads the instrument catalogue (config/instruments.csv, written by python/tools/make_instrument_catalogue.py):
/// a comma-separated file with a header naming the columns ticker, exchange, currency, name, isin, ibkr_con_id.
/// Lines starting with # are notes. Any line that cannot be read stops the whole file — with its line number.
/// </summary>
public static class InstrumentCatalogueFile
{
    private static string[] Columns { get; } = ["ticker", "exchange", "currency", "name", "isin", "ibkr_con_id"];

    public static IReadOnlyList<InstrumentListing> ReadFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return Read(reader);
    }

    public static IReadOnlyList<InstrumentListing> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var listings = new List<InstrumentListing>();
        Dictionary<string, int>? header = null;
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var fields = Split(line, number);
            if (header is null)
            {
                header = Header(fields, number);
                continue;
            }

            if (fields.Count != header.Count)
            {
                throw new FormatException($"Line {number}: {fields.Count} values, the header names {header.Count}.");
            }

            string Value(string column) => fields[header[column]].Trim();
            try
            {
                var conId = Value("ibkr_con_id");
                listings.Add(new InstrumentListing(
                    new Ticker(Value("ticker")),
                    Value("exchange"),
                    Currency.Of(Value("currency")),
                    Value("name"),
                    Value("isin").Length == 0 ? null : new Isin(Value("isin")),
                    conId.Length == 0 ? null : int.Parse(conId, NumberStyles.None, CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
            {
                throw new FormatException($"Line {number}: {e.Message}", e);
            }
        }

        if (header is null)
        {
            throw new FormatException("The file has no header line.");
        }

        return listings;
    }

    private static Dictionary<string, int> Header(List<string> fields, int number)
    {
        var header = fields
            .Select((name, index) => (Name: name.Trim().ToLowerInvariant(), index))
            .ToDictionary(c => c.Name, c => c.index, StringComparer.Ordinal);
        var missing = Columns.Where(c => !header.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new FormatException($"Line {number}: the header lacks the column(s) {string.Join(", ", missing)}.");
        }

        return header;
    }

    /// <summary>One CSV line into its values: commas separate, double quotes enclose, two quotes are one.</summary>
    private static List<string> Split(string line, int number)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            throw new FormatException($"Line {number}: a quoted value is not closed.");
        }

        fields.Add(field.ToString());
        return fields;
    }
}

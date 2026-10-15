using System.Globalization;

namespace HomeBlaze.History.Import.Influx;

/// <summary>
/// The CLR type a mapped v2 property has, which decides the column the store routes its values to.
/// Enum properties use <see cref="String"/>: the store keeps an enum as its name, which is the same JSON.
/// </summary>
internal enum MappingValueType
{
    Decimal,
    Double,
    Long,
    Int,
    Bool,
    String
}

/// <summary>
/// One mapping from an InfluxDB series (measurement and field) to a v2 property path. Numeric values
/// are divided by <c>Divisor</c>; string values are translated through <c>Values</c> when given, with
/// unmatched ones replaced by <c>FallbackValue</c>, or skipped without one.
/// </summary>
internal sealed record MappingRow(
    int LineNumber,
    bool Enabled,
    string Confidence,
    string Measurement,
    string Field,
    string Path,
    MappingValueType Type,
    decimal Divisor,
    IReadOnlyDictionary<string, string>? Values,
    string? FallbackValue,
    string Note)
{
    public Type PropertyType => Type switch
    {
        MappingValueType.Decimal => typeof(decimal?),
        MappingValueType.Double => typeof(double?),
        MappingValueType.Long => typeof(long?),
        MappingValueType.Int => typeof(int?),
        MappingValueType.Bool => typeof(bool?),
        _ => typeof(string)
    };
}

/// <summary>
/// Reads the mapping CSV: a header row naming the columns <c>enabled, confidence, measurement, field,
/// path, type, divisor, values, note</c>, then one row per mapping. Fields may be quoted.
/// </summary>
internal static class MappingFile
{
    private static readonly string[] RequiredColumns =
        ["enabled", "confidence", "measurement", "field", "path", "type"];

    public static IReadOnlyList<MappingRow> Parse(TextReader reader)
    {
        var records = CsvRecordReader.ReadAll(reader);
        if (records.Count == 0)
        {
            throw new FormatException("The mapping file is empty.");
        }

        var columns = records[0].Fields
            .Select((name, index) => (Name: name.Trim().ToLowerInvariant(), Index: index))
            .ToDictionary(column => column.Name, column => column.Index);
        foreach (var required in RequiredColumns)
        {
            if (!columns.ContainsKey(required))
            {
                throw new FormatException($"The mapping file has no '{required}' column.");
            }
        }

        var rows = new List<MappingRow>(records.Count - 1);
        foreach (var record in records.Skip(1))
        {
            if (record.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string Get(string name) =>
                columns.TryGetValue(name, out var index) && index < record.Fields.Count
                    ? record.Fields[index].Trim()
                    : string.Empty;

            try
            {
                var (values, fallback) = ParseValues(Get("values"));
                var divisorText = Get("divisor");
                rows.Add(new MappingRow(
                    record.LineNumber,
                    bool.Parse(Get("enabled")),
                    Get("confidence"),
                    Get("measurement"),
                    Get("field"),
                    Get("path"),
                    ParseType(Get("type")),
                    divisorText.Length == 0 ? 1m : decimal.Parse(divisorText, NumberStyles.Float, CultureInfo.InvariantCulture),
                    values,
                    fallback,
                    Get("note")));
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Mapping line {record.LineNumber}: {exception.Message}", exception);
            }
        }

        return rows;
    }

    private static MappingValueType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "decimal" => MappingValueType.Decimal,
        "double" => MappingValueType.Double,
        "long" => MappingValueType.Long,
        "int" => MappingValueType.Int,
        "bool" => MappingValueType.Bool,
        "string" => MappingValueType.String,
        _ => throw new FormatException($"'{type}' is not a supported type (decimal, double, long, int, bool, string).")
    };

    // "from=to;from=to;*=fallback"
    private static (IReadOnlyDictionary<string, string>? Values, string? Fallback) ParseValues(string text)
    {
        if (text.Length == 0)
        {
            return (null, null);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fallback = null;
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"'{pair}' in the values column is not 'from=to'.");
            }

            var from = pair[..separator];
            var to = pair[(separator + 1)..];
            if (from == "*")
            {
                fallback = to;
            }
            else
            {
                values[from] = to;
            }
        }

        return (values, fallback);
    }
}

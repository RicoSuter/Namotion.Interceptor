using System.Globalization;
using System.Text.Json;

namespace HomeBlaze.History.Import.Influx;

/// <summary>Writes the per-row import statistics as a markdown report.</summary>
internal static class ReportWriter
{
    public static void Write(
        TextWriter writer,
        IReadOnlyList<MappingRow> rows,
        IReadOnlyList<RowStatistics> statistics,
        DateTimeOffset since,
        DateTimeOffset from,
        DateTimeOffset until,
        bool isDryRun)
    {
        writer.WriteLine("# v1 history import report");
        writer.WriteLine();
        writer.WriteLine(Invariant($"- Mode: {(isDryRun ? "dry run, nothing written" : "import")}"));
        writer.WriteLine(Invariant($"- Data read: {Time(from)} to {Time(until)} (exclusive)"));
        writer.WriteLine(Invariant($"- Coverage claimed: {Time(since)} to {Time(until)}{(isDryRun ? " (not written in a dry run)" : "")}"));
        writer.WriteLine(Invariant($"- Enabled rows: {statistics.Count}, disabled rows: {rows.Count(row => !row.Enabled)}"));
        writer.WriteLine(Invariant($"- Points: {statistics.Sum(row => row.PointCount)}, inserted: {statistics.Sum(row => row.InsertedCount)}, ignored: {statistics.Sum(row => row.IgnoredCount)}, skipped: {statistics.Sum(row => row.SkippedCount)}"));
        writer.WriteLine();
        writer.WriteLine("| v2 path | v1 series | type | points | inserted | ignored | skipped | first | last | first value | last value | skipped examples |");
        writer.WriteLine("|---|---|---|---:|---:|---:|---:|---|---|---|---|---|");
        foreach (var row in statistics.OrderBy(row => row.Row.Path, StringComparer.Ordinal))
        {
            var divisor = row.Row.Divisor != 1m ? Invariant($" / {row.Row.Divisor}") : "";
            writer.WriteLine(
                Invariant($"| {Cell(row.Row.Path)} | {Cell(row.Row.Measurement)} {Cell(row.Row.Field)}{divisor} | {row.Row.Type} | ") +
                Invariant($"{row.PointCount} | {row.InsertedCount} | {row.IgnoredCount} | {row.SkippedCount} | ") +
                Invariant($"{Time(row.First)} | {Time(row.Last)} | {Value(row.FirstValue)} | {Value(row.LastValue)} | ") +
                Invariant($"{Cell(string.Join(", ", row.SkippedExamples))} |"));
        }
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";

    private static string Value(object? value)
    {
        var text = value switch
        {
            null => "null",
            string stringValue => JsonSerializer.Serialize(stringValue, FluxJsonContext.Default.String),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        return Cell(text.Length > 60 ? text[..60] + "..." : text);
    }

    private static string Cell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

using System.Globalization;

namespace HomeBlaze.History.Import.Influx;

/// <summary>A point read from a Flux result: its time and the raw CSV text of its value.</summary>
internal readonly record struct FluxPoint(DateTimeOffset Timestamp, string Value);

/// <summary>
/// Streams the <c>_time</c> and <c>_value</c> columns out of a Flux CSV response without annotations.
/// Each table starts with its own header row, so column positions are re-read per table.
/// </summary>
internal static class FluxCsvReader
{
    public static IEnumerable<FluxPoint> Read(TextReader reader, CancellationToken cancellationToken)
    {
        var timeIndex = -1;
        var valueIndex = -1;
        var errorIndex = -1;
        var lineNumber = 0;
        while (CsvRecordReader.TryRead(reader, ref lineNumber, out var record))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = record.Fields;
            if (fields.Count <= 1)
            {
                // An empty line separates tables; the next record is a header again.
                timeIndex = valueIndex = errorIndex = -1;
                continue;
            }

            if (timeIndex < 0 && valueIndex < 0 && errorIndex < 0)
            {
                timeIndex = fields.IndexOf("_time");
                valueIndex = fields.IndexOf("_value");
                errorIndex = fields.IndexOf("error");
                continue;
            }

            if (errorIndex >= 0)
            {
                throw new InvalidOperationException($"InfluxDB reported an error: {fields[errorIndex]}");
            }

            if (timeIndex < 0 || valueIndex < 0)
            {
                throw new FormatException($"Flux table on line {record.LineNumber} has no _time or _value column.");
            }

            yield return new FluxPoint(ParseTime(fields[timeIndex]), fields[valueIndex]);
        }
    }

    // RFC 3339 with up to nine fraction digits. DateTimeOffset parses at most seven, the tick
    // resolution the store keeps anyway, so the rest is truncated before parsing.
    internal static DateTimeOffset ParseTime(string text)
    {
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}

using System.Text;

namespace HomeBlaze.History.Import.Influx;

/// <summary>A CSV record and the line it starts on.</summary>
internal readonly record struct CsvRecord(int LineNumber, List<string> Fields);

/// <summary>
/// Minimal RFC 4180 reader: comma separated, double-quoted fields may contain commas, line breaks and
/// doubled quotes. An empty line yields a record with a single empty field.
/// </summary>
internal static class CsvRecordReader
{
    public static List<CsvRecord> ReadAll(TextReader reader)
    {
        var records = new List<CsvRecord>();
        var lineNumber = 0;
        while (TryRead(reader, ref lineNumber, out var record))
        {
            records.Add(record);
        }

        return records;
    }

    public static bool TryRead(TextReader reader, ref int lineNumber, out CsvRecord record)
    {
        var line = reader.ReadLine();
        if (line is null)
        {
            record = default;
            return false;
        }

        lineNumber++;
        var startLine = lineNumber;
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = ParseLine(line, fields, field, inQuotes: false);
        while (inQuotes)
        {
            // A quoted field continues on the next physical line.
            line = reader.ReadLine() ??
                throw new FormatException($"Unterminated quoted field starting on line {startLine}.");
            lineNumber++;
            field.Append('\n');
            inQuotes = ParseLine(line, fields, field, inQuotes: true);
        }

        fields.Add(field.ToString());
        record = new CsvRecord(startLine, fields);
        return true;
    }

    // Appends the line's characters to the current field, completing fields at unquoted commas, and
    // returns whether the line ended inside a quoted field.
    private static bool ParseLine(string line, List<string> fields, StringBuilder field, bool inQuotes)
    {
        var position = 0;
        while (position < line.Length)
        {
            var character = line[position++];
            if (inQuotes)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (position < line.Length && line[position] == '"')
                {
                    field.Append('"');
                    position++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        return inQuotes;
    }
}

using System.Globalization;
using System.Text;

namespace Namotion.Devices.SunSpec.Generator;

internal static class CSharpText
{
    /// <summary>
    /// Returns a SunSpec point name unchanged, failing when it is not a valid C# identifier.
    /// </summary>
    public static string ToIdentifier(string name)
    {
        var isValid = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
                      name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        return isValid ? name : throw new InvalidDataException($"'{name}' is not a valid C# identifier.");
    }

    /// <summary>
    /// Converts a SunSpec symbol name such as "Volt-VAr" or "%WMax" into an enum member name ("Volt_VAr", "PctWMax").
    /// </summary>
    public static string ToEnumMemberName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Replace("%", "Pct", StringComparison.Ordinal))
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (builder.Length == 0 || char.IsAsciiDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    public static string Literal(string text)
        => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Escapes text for the inside of an interpolated string literal.
    /// </summary>
    public static string InterpolationText(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    /// <summary>
    /// Collapses whitespace and escapes text for an XML documentation comment.
    /// </summary>
    public static string XmlText(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static string NumberLiteral(decimal value) => ((double)value).ToString("R", CultureInfo.InvariantCulture);

    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);
}

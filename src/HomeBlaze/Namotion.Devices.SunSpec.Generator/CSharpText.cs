using System.Globalization;
using System.Text;

namespace Namotion.Devices.SunSpec.Generator;

internal static class CSharpText
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while"
    };

    private static readonly Dictionary<char, string> EscapeSequences = new()
    {
        ['\\'] = "\\\\", ['"'] = "\\\"", ['\n'] = "\\n", ['\r'] = "\\r", ['\t'] = "\\t"
    };

    /// <summary>
    /// Returns a SunSpec name unchanged, failing when it is not a valid C# identifier or is a C# keyword.
    /// </summary>
    public static string ToIdentifier(string name)
    {
        var isValid = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
                      name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        if (!isValid)
        {
            throw new InvalidDataException($"'{name}' is not a valid C# identifier.");
        }

        return Keywords.Contains(name) ? throw new InvalidDataException($"'{name}' is a C# keyword and cannot be a generated identifier.") : name;
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

    /// <summary>
    /// Returns a regular string literal of <paramref name="text"/>.
    /// </summary>
    public static string Literal(string text) => "\"" + Escape(text, isInterpolated: false) + "\"";

    /// <summary>
    /// Escapes text for the inside of an interpolated string literal.
    /// </summary>
    public static string InterpolationText(string text) => Escape(text, isInterpolated: true);

    /// <summary>
    /// Collapses whitespace and escapes text for an XML documentation comment.
    /// </summary>
    public static string XmlText(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static string NumberLiteral(decimal value) => ((double)value).ToString("R", CultureInfo.InvariantCulture);

    public static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string text, bool isInterpolated)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (GetEscapeSequence(character, isInterpolated) is { } escapeSequence)
            {
                builder.Append(escapeSequence);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string? GetEscapeSequence(char character, bool isInterpolated)
    {
        if (EscapeSequences.TryGetValue(character, out var escapeSequence))
        {
            return escapeSequence;
        }

        if (isInterpolated && character is '{' or '}')
        {
            return new string(character, 2);
        }

        return IsUnprintable(character) ? "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture) : null;
    }

    // Line separators are new lines to the C# compiler, so they cannot appear unescaped in a string literal.
    private static bool IsUnprintable(char character)
        => char.IsControl(character) || character is '\u2028' or '\u2029';
}

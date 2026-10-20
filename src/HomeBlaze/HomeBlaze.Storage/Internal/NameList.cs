using System.Buffers;
using System.Globalization;
using System.Text;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Formats names that a caller gave, such as keys or file names, for a message.
/// </summary>
internal static class NameList
{
    private const int MaxNameLength = 64;
    private const int MaxNameCount = 10;

    /// <summary>
    /// Formats names to show in a message, each in quotes, shortened to 64 characters and with control, format and line
    /// or paragraph separator characters escaped as <c>\uXXXX</c>, at most 10 of them followed by an ellipsis; "none" for no name.
    /// </summary>
    public static string Format(IEnumerable<string> names)
    {
        var formattedNames = names
            .Take(MaxNameCount + 1)
            .Select(static (name, index) => index == MaxNameCount
                ? "…"
                : $"'{EscapeHiddenCharacters(Shorten(name))}'")
            .ToList();
        return formattedNames.Count > 0 ? string.Join(", ", formattedNames) : "none";
    }

    // Not within a surrogate pair, which would leave a lone surrogate.
    private static string Shorten(string name)
    {
        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        var length = char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
        return name[..length] + "…";
    }

    // Control, format (such as a bidirectional override) and line or paragraph separator characters, which change how
    // a message shows or hide text in it. Each UTF-16 code unit of such a character is escaped, a pair as two.
    private static string EscapeHiddenCharacters(string text)
    {
        StringBuilder? builder = null;
        var index = 0;
        while (index < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var length);
            if (status == OperationStatus.Done && IsEscaped(rune))
            {
                builder ??= new StringBuilder(text.Length + 10).Append(text, 0, index);
                for (var offset = 0; offset < length; offset++)
                {
                    builder.Append($"\\u{(int)text[index + offset]:X4}");
                }
            }
            else
            {
                builder?.Append(text, index, length);
            }

            index += length;
        }

        return builder?.ToString() ?? text;
    }

    private static bool IsEscaped(Rune rune)
        => Rune.IsControl(rune) ||
           Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}

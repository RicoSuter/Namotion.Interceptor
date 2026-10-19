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
    /// Formats names to show in a message, each in quotes, shortened to 64 characters and with control characters
    /// escaped as <c>\uXXXX</c>, at most 10 of them followed by an ellipsis; "none" for no name.
    /// </summary>
    public static string Format(IEnumerable<string> names)
    {
        var formattedNames = names
            .Take(MaxNameCount + 1)
            .Select(static (name, index) => index == MaxNameCount
                ? "…"
                : $"'{EscapeControlCharacters(Shorten(name))}'")
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

    private static string EscapeControlCharacters(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 10);
        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                builder.Append($"\\u{(int)character:X4}");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

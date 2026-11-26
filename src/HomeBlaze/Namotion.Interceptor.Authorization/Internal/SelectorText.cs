using System.Globalization;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Rules for the text of names and selector values, so a value cannot look like another and silently match nothing.</summary>
internal static class SelectorText
{
    /// <summary>Whether the text is empty, is whitespace only, has leading or trailing whitespace, or contains a control or format character.</summary>
    public static bool IsInvalid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
        {
            return true;
        }

        return ContainsInvisibleCharacter(text);
    }

    /// <summary>Whether the text contains a character of the Unicode category control or format.</summary>
    public static bool ContainsInvisibleCharacter(string text)
    {
        foreach (var character in text)
        {
            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }
}

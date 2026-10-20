using System.Globalization;

namespace HomeBlaze.Services;

/// <summary>
/// The rules of the links and tags of metadata, which a write refuses to break and a read of a file edited by hand
/// applies by leaving out what breaks them.
/// </summary>
public static class MetadataText
{
    /// <summary>The most links, and the most tags.</summary>
    public const int MaxItemCount = 20;

    /// <summary>The longest tag.</summary>
    public const int MaxTagLength = 64;

    /// <summary>Gets whether the tag, without whitespace around it, has 1 to 64 characters and no control or format character.</summary>
    public static bool IsAllowedTag(string tag)
        => tag.Length is > 0 and <= MaxTagLength && !HasControlOrFormatCharacter(tag);

    /// <summary>
    /// Gets whether the text has a control or a format character, such as a line break, a bidirectional override or a
    /// zero-width character, which can make a text look like another.
    /// </summary>
    public static bool HasControlOrFormatCharacter(string text)
    {
        foreach (var character in text)
        {
            if (char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }
}

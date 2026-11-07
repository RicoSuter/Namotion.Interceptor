using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace HomeBlaze.Authorization;

/// <summary>
/// The rule for user names and role names, built-in and custom: lowercase ASCII letters <c>a-z</c>, digits <c>0-9</c>,
/// <c>.</c>, <c>-</c> and <c>_</c>, starting with a letter or a digit, 1 to <see cref="MaximumLength"/> characters. Names
/// are compared ordinally. Also the rule for the display names of users, which people read but nothing compares.
/// </summary>
public static class NameRules
{
    /// <summary>The name of the built-in user of visitors who are not signed in.</summary>
    public const string AnonymousUserName = "anonymous";

    /// <summary>The name of the built-in user of the external MCP endpoint.</summary>
    public const string McpUserName = "mcp";

    /// <summary>The maximum length of a name.</summary>
    public const int MaximumLength = 64;

    /// <summary>The rule, as a person reads it.</summary>
    public const string RuleDescription =
        "Use lowercase letters a-z, digits, '.', '-' or '_', starting with a letter or digit, at most 64 characters.";

    /// <summary>The maximum length of a display name, in UTF-16 code units.</summary>
    public const int MaximumDisplayNameLength = 100;

    // Enough for the marks a script stacks on one letter, such as Vietnamese or Tibetan, but not for text that covers the lines around it.
    private const int MaximumConsecutiveMarks = 4;

    private const string DisplayNameCharacterError = "The display name contains a control, invisible, private use or unassigned character.";

    // Format holds the zero-width characters and the bidirectional controls, such as U+202E.
    private static readonly UnicodeCategory[] RefusedDisplayNameCategories =
    [
        UnicodeCategory.Control,
        UnicodeCategory.Format,
        UnicodeCategory.LineSeparator,
        UnicodeCategory.ParagraphSeparator,
        UnicodeCategory.PrivateUse,
        UnicodeCategory.OtherNotAssigned
    ];

    // Letters and symbols that render blank: the Hangul fillers and the blank Braille pattern.
    private static readonly int[] BlankCharacters = [0x115F, 0x1160, 0x2800, 0x3164, 0xFFA0];

    /// <summary>The names of the built-in users.</summary>
    public static ImmutableArray<string> BuiltInUserNames { get; } = [AnonymousUserName, McpUserName];

    /// <summary>Whether <paramref name="name"/> is the name of a built-in user.</summary>
    public static bool IsBuiltInUserName(string name) =>
        string.Equals(name, AnonymousUserName, StringComparison.Ordinal) ||
        string.Equals(name, McpUserName, StringComparison.Ordinal);

    /// <summary>Gets why <paramref name="name"/> breaks the rule, or null.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kindOfName">What the name names in the message, such as <c>user name</c>.</param>
    public static string? GetNameError(string? name, string kindOfName)
    {
        if (string.IsNullOrEmpty(name))
        {
            return $"A {kindOfName} is required.";
        }

        return IsValid(name) ? null : $"The {kindOfName} is not valid. {RuleDescription}";
    }

    private static bool IsValid(string name)
    {
        if (name.Length > MaximumLength || !IsLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetterOrDigit(char character) => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character);

    /// <summary>Gets <paramref name="displayName"/> trimmed, or null when it is null or only white space.</summary>
    public static string? NormalizeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    /// <summary>
    /// Gets why <paramref name="displayName"/>, normalized with <see cref="NormalizeDisplayName"/>, cannot be a display
    /// name, or null; null is none. Letters of any script are allowed; refused are more than
    /// <see cref="MaximumDisplayNameLength"/> code units, invalid UTF-16, control, format (such as zero-width and
    /// bidirectional controls), line separator, paragraph separator, private use and unassigned characters, more than four
    /// marks in a row, and a name of only marks, spaces and characters that render blank, such as the Hangul fillers.
    /// </summary>
    public static string? GetDisplayNameError(string? displayName)
    {
        if (displayName is null)
        {
            return null;
        }

        if (displayName.Length > MaximumDisplayNameLength)
        {
            return $"A display name has at most {MaximumDisplayNameLength} characters.";
        }

        var hasVisibleCharacter = false;
        var consecutiveMarks = 0;
        var remaining = displayName.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed) != OperationStatus.Done)
            {
                return DisplayNameCharacterError;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (Array.IndexOf(RefusedDisplayNameCategories, category) >= 0)
            {
                return DisplayNameCharacterError;
            }

            if (IsMark(category))
            {
                if (++consecutiveMarks > MaximumConsecutiveMarks)
                {
                    return $"The display name stacks more than {MaximumConsecutiveMarks} marks on a character.";
                }
            }
            else
            {
                consecutiveMarks = 0;
                hasVisibleCharacter |= category != UnicodeCategory.SpaceSeparator && Array.IndexOf(BlankCharacters, rune.Value) < 0;
            }

            remaining = remaining[charsConsumed..];
        }

        return hasVisibleCharacter ? null : "The display name has no visible character.";
    }

    private static bool IsMark(UnicodeCategory category) =>
        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}

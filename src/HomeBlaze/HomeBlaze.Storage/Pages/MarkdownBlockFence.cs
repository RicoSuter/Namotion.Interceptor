namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The fence of a block: the run of fence characters that starts its head line and makes up its closing line.
/// </summary>
internal static class MarkdownBlockFence
{
    /// <summary>The fence character.</summary>
    public const char Character = ':';

    /// <summary>The shortest fence. A block inside another block has more characters than the block around it.</summary>
    public const int MinimumLength = 2;

    /// <summary>Gets a fence of the given length.</summary>
    public static string Create(int length) => new(Character, length);

    /// <summary>
    /// Gets whether text after a fence that starts with <paramref name="character"/> makes the line a head line.
    /// Other text, such as a time or a smiley, makes the line no fence line.
    /// </summary>
    public static bool StartsHeadLine(char character) => char.IsLetter(character) || character == '_' || character == '{';

    /// <summary>
    /// Gets whether a fence after this many columns of indentation is code and no fence line. A tab counts up to
    /// the next multiple of four columns, so a tab in the indentation always makes the fence code.
    /// </summary>
    public static bool IsIndentedAsCode(int indentationColumns) => indentationColumns >= 4;
}

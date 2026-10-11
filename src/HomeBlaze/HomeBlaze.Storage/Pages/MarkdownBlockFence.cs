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
}

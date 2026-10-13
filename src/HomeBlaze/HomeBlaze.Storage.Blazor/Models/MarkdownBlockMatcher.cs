using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Decides whether a block of the editor text is a block of the saved page text. The index of a block
/// and the subject behind it belong to the saved text, and they apply to the editor text only while
/// both texts are the same up to that block.
/// </summary>
public static class MarkdownBlockMatcher
{
    /// <summary>
    /// Gets the block of <paramref name="editorText"/> with this index when both texts are identical
    /// from the start through the end of its head line and json fence. Otherwise null.
    /// </summary>
    public static MarkdownBlock? FindSavedBlock(string editorText, string? savedText, int blockIndex)
    {
        if (savedText is null || FindBlocks(editorText, savedText, blockIndex) is not var (editorBlock, savedBlock))
        {
            return null;
        }

        var end = editorBlock.ConfigurationSpan?.End ?? editorBlock.HeadSpan.End;
        return editorBlock.Kind == savedBlock.Kind &&
               editorBlock.HeadSpan == savedBlock.HeadSpan &&
               editorBlock.ConfigurationSpan == savedBlock.ConfigurationSpan &&
               HaveSameStart(editorText, savedText, end)
            ? editorBlock
            : null;
    }

    /// <summary>
    /// Gets the block of <paramref name="editorText"/> with this index when both texts are identical up
    /// to the start of its head line and the block has the same kind, name and id in both. Otherwise null.
    /// </summary>
    /// <remarks>
    /// For the time after the configuration of the block was written to the saved text. The write
    /// formats the head line and the json fence anew, so they no longer equal the editor text.
    /// </remarks>
    public static MarkdownBlock? FindSavedBlockAfterWriteBack(string editorText, string? savedText, int blockIndex)
    {
        if (savedText is null || FindBlocks(editorText, savedText, blockIndex) is not var (editorBlock, savedBlock))
        {
            return null;
        }

        return editorBlock.Kind == savedBlock.Kind &&
               editorBlock.Head?.Name == savedBlock.Head?.Name &&
               editorBlock.Head?.Id == savedBlock.Head?.Id &&
               editorBlock.HeadSpan.Start == savedBlock.HeadSpan.Start &&
               HaveSameStart(editorText, savedText, editorBlock.HeadSpan.Start)
            ? editorBlock
            : null;
    }

    private static (MarkdownBlock Editor, MarkdownBlock Saved)? FindBlocks(string editorText, string savedText, int blockIndex)
    {
        if (blockIndex < 0)
        {
            return null;
        }

        var editorBlocks = MarkdownPageParser.Parse(editorText).AllBlocks;
        if (blockIndex >= editorBlocks.Count || editorBlocks[blockIndex].Head is null)
        {
            return null;
        }

        var savedBlocks = MarkdownPageParser.Parse(savedText).AllBlocks;
        return blockIndex < savedBlocks.Count ? (editorBlocks[blockIndex], savedBlocks[blockIndex]) : null;
    }

    // The caller passes a length that is a span position of both texts, so it is inside both.
    private static bool HaveSameStart(string editorText, string savedText, int length) =>
        editorText.AsSpan(0, length).SequenceEqual(savedText.AsSpan(0, length));
}

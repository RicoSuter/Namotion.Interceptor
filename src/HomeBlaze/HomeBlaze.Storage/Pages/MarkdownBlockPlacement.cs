namespace HomeBlaze.Storage.Pages;

/// <summary>Where a new slot block goes and how it is written.</summary>
/// <param name="Line">The 1-based line before which the block is inserted. A line after the last line appends it.</param>
/// <param name="FenceLength">The number of fence characters of the block. A block inside it has one more.</param>
/// <param name="HasClosingLine">Whether the block and the blocks in it get a closing line.</param>
/// <param name="Indentation">The white space that every line of the new block gets before it.</param>
/// <param name="LineEnding">The line ending of the page at this place.</param>
internal readonly record struct MarkdownBlockPlacement(
    int Line, int FenceLength, bool HasClosingLine, string Indentation, string LineEnding);

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// The lines of one block of a page and where its fences are, for showing its nesting in the editor.
/// </summary>
/// <param name="StartLine">1-based head line.</param>
/// <param name="EndLine">1-based last line: the closing line, or without one the last line of the content that is not empty.</param>
/// <param name="ConfigurationStartLine">1-based first line of the json block of the block, or null without one.</param>
/// <param name="HeadEndLine">1-based line where the head line ends, or its json block when the block has one.</param>
/// <param name="HeadEndColumn">1-based column after the last character of the head line or of its json block.</param>
/// <param name="Depth">0 for a block directly on the page, one more for each block it is inside of.</param>
/// <param name="Name">The name of the block, or the type name of a block without a name, or the attributes of a style block.</param>
/// <param name="HeadFence">The fence of the head line.</param>
/// <param name="ClosingFence">The fence of the closing line of the block, or null when the block has none of its own.</param>
public record BlockOutline(
    int StartLine,
    int EndLine,
    int? ConfigurationStartLine,
    int HeadEndLine,
    int HeadEndColumn,
    int Depth,
    string Name,
    FencePosition HeadFence,
    FencePosition? ClosingFence);

/// <summary>
/// The place of the fence characters on a line.
/// </summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column of the first fence character.</param>
/// <param name="Length">Number of fence characters.</param>
public readonly record struct FencePosition(int Line, int Column, int Length);

/// <summary>
/// The decorations of a page: the regions that can be edited and the outline of every block.
/// </summary>
public sealed record MarkdownDecorations(List<DecorationRegion> Regions, List<BlockOutline> Outlines);

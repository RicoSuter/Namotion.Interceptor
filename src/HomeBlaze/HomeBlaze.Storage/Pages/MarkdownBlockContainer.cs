using Markdig.Parsers;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The node of a block in the parsed document: everything between its head line and its closing line.
/// A closing line that closes no block is a node of its own without content.
/// </summary>
internal sealed class MarkdownBlockContainer(BlockParser parser) : ContainerBlock(parser)
{
    public required int FenceLength { get; init; }

    /// <summary>Whether the node is a closing line that closes no block.</summary>
    public bool IsClosingLine { get; init; }

    /// <summary>The blocks that were open at a closing line that closes no block, outermost first.</summary>
    public IReadOnlyList<MarkdownBlockContainer> OpenBlocks { get; init; } = [];

    /// <summary>Whether the head line has attributes in braces in place of a name.</summary>
    public bool IsStyle { get; init; }

    /// <summary>Whether a closing line ended the block.</summary>
    public bool IsClosed { get; set; }

    /// <summary>
    /// 0-based line at which the block ended: its closing line, or the line that ended it in another way,
    /// which is one after the last line when the page ended first.
    /// </summary>
    public int EndLine { get; set; }
}

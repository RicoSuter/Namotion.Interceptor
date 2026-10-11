using Markdig.Parsers;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The node of a block in the parsed document: its head line and everything up to its closing line, or
/// up to its last line of content when it has no closing line. A closing line that closes no block is
/// a node of its own without content.
/// </summary>
/// <remarks>
/// The span of a block ends with its closing line. Without a closing line it ends where Markdig leaves
/// it, which is not always the last line of the block, so read <see cref="EndLine"/> for that.
/// </remarks>
internal sealed class MarkdownBlockContainer(BlockParser parser) : ContainerBlock(parser)
{
    // The value of EndLine before the last call of AddLine.
    private int _endLineBeforeLastLine;

    public required int FenceLength { get; init; }

    /// <summary>Whether the node is a closing line that closes no block.</summary>
    public bool IsClosingLine { get; init; }

    /// <summary>
    /// The blocks that a closing line that closes no block could have closed, outermost first: the open
    /// blocks that it is directly inside of.
    /// </summary>
    public IReadOnlyList<MarkdownBlockContainer> OpenBlocks { get; init; } = [];

    /// <summary>
    /// Whether a closing line that closes no block is inside a quote, a list item or another container
    /// that is not a block, with an open block around that container which it does not reach.
    /// </summary>
    public bool HasOpenBlockOutOfReach { get; init; }

    /// <summary>Whether the head line has attributes in braces in place of a name.</summary>
    public bool IsStyle { get; init; }

    /// <summary>Whether a closing line of its own ended the block.</summary>
    public bool HasClosingLine { get; set; }

    /// <summary>
    /// 0-based last line of the block: its closing line, or without one the last line of its content
    /// that is not empty, which is its head line when it has no content.
    /// </summary>
    /// <remarks>
    /// Without a closing line the value is exact for a block that is reached through blocks alone. Inside
    /// a quote, a list item or a table cell it can be an earlier line of the block, because Markdig does
    /// not ask the block for every line there.
    /// </remarks>
    public required int EndLine { get; set; }

    /// <summary>Makes a line that is not empty the last line of the block.</summary>
    public void AddLine(int line)
    {
        _endLineBeforeLastLine = EndLine;
        EndLine = line;
    }

    /// <summary>Takes the last line off the block again, when it turned out to be the line that ended the block.</summary>
    public void RemoveLastLine() => EndLine = _endLineBeforeLastLine;
}

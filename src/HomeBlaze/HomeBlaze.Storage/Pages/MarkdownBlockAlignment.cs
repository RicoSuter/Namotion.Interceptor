using System.Text;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Maps the blocks of a page to the blocks of the page after one change of its lines, and checks that the
/// change did nothing to the blocks and the text that it did not insert or remove.
/// </summary>
internal static class MarkdownBlockAlignment
{
    /// <summary>
    /// Gets for every block index of <paramref name="before"/> the index of the same block in
    /// <paramref name="after"/>, and -1 for a block on a removed line.
    /// </summary>
    /// <param name="before">The page before the change.</param>
    /// <param name="after">The page after the change.</param>
    /// <param name="edit">What the change did to the lines.</param>
    /// <param name="expectedInsertedBlockCount">How many blocks of every kind the inserted lines must hold.</param>
    /// <param name="owner">The block of <paramref name="before"/> that the lines were inserted into, or null for a removal.</param>
    /// <returns>
    /// Null when the change did more than insert or remove its blocks, that is when either
    /// <see cref="TryMapBlocks"/> or <see cref="HasSameDocument"/> says no.
    /// </returns>
    public static int[]? TryAlign(
        MarkdownPage before, MarkdownPage after, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner)
    {
        var map = TryMapBlocks(before, after, edit, expectedInsertedBlockCount, owner);
        return map is not null && HasSameDocument(before, after, edit) ? map : null;
    }

    /// <summary>
    /// The walk over the blocks of the page: gets for every block index of <paramref name="before"/> the index
    /// of the same block in <paramref name="after"/>, and -1 for a block on a removed line.
    /// </summary>
    /// <returns>
    /// Null when a block of the page tells that the change did more than insert or remove its blocks. Every
    /// other block, of every kind, must have the same kind, fence, head line, closing line and json block as
    /// before, the lines that the change explains, and the same block around it. Every inserted block must be a
    /// slot block directly inside <paramref name="owner"/> or stand inside another inserted block, and must end
    /// on the inserted lines.
    /// </returns>
    internal static int[]? TryMapBlocks(
        MarkdownPage before, MarkdownPage after, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner)
    {
        var oldBlocks = Flatten(before.Blocks);
        var newBlocks = Flatten(after.Blocks);
        var removedEnd = edit.Line + edit.RemovedLineCount;
        var insertedEnd = edit.Line + edit.InsertedLineCount;
        var blocksAroundOwner = FindBlocksAround(oldBlocks, owner);

        // The block of the page after the change for every block that stays.
        var counterparts = new Dictionary<MarkdownBlock, MarkdownBlock>(ReferenceEqualityComparer.Instance);
        var insertedBlocks = new HashSet<MarkdownBlock>(ReferenceEqualityComparer.Instance);
        var map = new int[before.AllBlocks.Count];
        Array.Fill(map, -1);
        var newPosition = 0;

        bool TakeInsertedBlocks()
        {
            while (newPosition < newBlocks.Count &&
                   newBlocks[newPosition].Block.Line >= edit.Line &&
                   newBlocks[newPosition].Block.Line < insertedEnd)
            {
                var (block, parent) = newBlocks[newPosition++];
                var isInsideInsertedBlock = parent is not null && insertedBlocks.Contains(parent);
                var isSlotBlockOfOwner =
                    block.Kind == MarkdownBlockKind.Slot &&
                    owner is not null &&
                    counterparts.TryGetValue(owner, out var newOwner) &&
                    ReferenceEquals(parent, newOwner);

                // A block that goes on after the inserted lines has taken over what follows it.
                if (block.EndLine >= insertedEnd || (!isInsideInsertedBlock && !isSlotBlockOfOwner))
                {
                    return false;
                }

                insertedBlocks.Add(block);
            }

            return true;
        }

        foreach (var (oldBlock, oldParent) in oldBlocks)
        {
            if (oldBlock.Line >= edit.Line && oldBlock.Line < removedEnd)
            {
                continue;
            }

            if (!TakeInsertedBlocks() || newPosition == newBlocks.Count)
            {
                return null;
            }

            var (newBlock, newParent) = newBlocks[newPosition++];
            var hasSameParent = oldParent is null
                ? newParent is null
                : counterparts.TryGetValue(oldParent, out var expectedParent) && ReferenceEquals(expectedParent, newParent);

            if (!hasSameParent ||
                !IsSameBlock(before, oldBlock, after, newBlock, edit, blocksAroundOwner.Contains(oldBlock)))
            {
                return null;
            }

            counterparts[oldBlock] = newBlock;
            if (oldBlock.Index >= 0)
            {
                map[oldBlock.Index] = newBlock.Index;
            }
        }

        return TakeInsertedBlocks() &&
               newPosition == newBlocks.Count &&
               insertedBlocks.Count == expectedInsertedBlockCount
            ? map
            : null;
    }

    // The kind of a block of the document, the containers around it from the document inwards, the classes and
    // the properties of its attributes, and for a leaf block where its text is.
    private readonly record struct DocumentBlockDescription(Type Kind, string Containers, string Attributes, int Start, int Length);

    /// <summary>
    /// The comparison of the Markdig document outside the change: gets whether every block of the document that
    /// does not start on a line of the change is what it was.
    /// </summary>
    /// <returns>
    /// False unless every leaf block of the document that does not start on a line of the change, such as a
    /// paragraph, a heading or a code block, has the same kind, the same text, the same classes and properties
    /// of its attributes and the same containers around it as before, and every container that does not start
    /// there, such as a list or a quote, is still a container of its own with the same classes and properties.
    /// Link reference definitions and the id of a block are not compared.
    /// </returns>
    internal static bool HasSameDocument(MarkdownPage before, MarkdownPage after, MarkdownLineEdit edit)
    {
        var oldBlocks = DescribeDocumentBlocks(before, edit.Line, edit.RemovedLineCount);
        var newBlocks = DescribeDocumentBlocks(after, edit.Line, edit.InsertedLineCount);
        if (oldBlocks.Count != newBlocks.Count)
        {
            return false;
        }

        for (var index = 0; index < oldBlocks.Count; index++)
        {
            var oldBlock = oldBlocks[index];
            var newBlock = newBlocks[index];
            if (oldBlock.Kind != newBlock.Kind ||
                !string.Equals(oldBlock.Containers, newBlock.Containers, StringComparison.Ordinal) ||
                !string.Equals(oldBlock.Attributes, newBlock.Attributes, StringComparison.Ordinal) ||
                !before.Source.AsSpan(oldBlock.Start, oldBlock.Length).SequenceEqual(after.Source.AsSpan(newBlock.Start, newBlock.Length)))
            {
                return false;
            }
        }

        return true;
    }

    // Every block of the Markdig document that does not start on the given lines, in the order of the document:
    // its kind, the containers around it and its attributes, and for a leaf block its text. A change that only
    // inserts or removes its own lines leaves this list as it was. The containers are listed themselves, because
    // two lists that become one keep every leaf block as it is.
    private static List<DocumentBlockDescription> DescribeDocumentBlocks(MarkdownPage page, int skippedLine, int skippedLineCount)
    {
        var source = page.Source;
        var result = new List<DocumentBlockDescription>();
        Add(page.Document, string.Empty);
        return result;

        void Add(ContainerBlock container, string containers)
        {
            foreach (var child in container)
            {
                // Link reference definitions are no text of the page, and Markdig lists their group where the
                // first definition or heading of the page made it, which a change can move.
                if (child is LinkReferenceDefinitionGroup)
                {
                    continue;
                }

                var line = child.Line + 1;
                var isSkipped = line >= skippedLine && line < skippedLine + skippedLineCount;

                if (child is ContainerBlock inner)
                {
                    // The group of the footnotes has no line of its own. Its footnotes are listed.
                    if (!isSkipped && child is not FootnoteGroup)
                    {
                        result.Add(new DocumentBlockDescription(child.GetType(), containers, DescribeAttributes(child), 0, 0));
                    }

                    // A block of the page is named by its head line, so that text which moves into the block
                    // beside it is seen. Every other container is named by its type.
                    var name = inner is MarkdownBlockContainer ? GetLine(source, inner.Span.Start).ToString() : inner.GetType().Name;
                    Add(inner, $"{containers}\n{name}");
                }
                else if (child is LeafBlock && !isSkipped)
                {
                    // Without white space at the end: the span of an indented code block takes the empty lines after it.
                    var start = Math.Clamp(child.Span.Start, 0, source.Length);
                    var end = Math.Clamp(child.Span.End + 1, start, source.Length);
                    var length = source.AsSpan(start, end - start).TrimEnd().Length;
                    result.Add(new DocumentBlockDescription(child.GetType(), containers, DescribeAttributes(child), start, length));
                }
            }
        }
    }

    // The classes and the properties of a block, which a line that holds only attributes gives to the block
    // after it, whatever that is after the change. Without the id: Markdig gives every heading one and numbers
    // the ids of headings with the same text, so a removed heading changes the id of another one.
    private static string DescribeAttributes(Block block)
    {
        var attributes = block.TryGetAttributes();
        var classes = attributes?.Classes;
        var properties = attributes?.Properties;
        if (classes is not { Count: > 0 } && properties is not { Count: > 0 })
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (classes is not null)
        {
            foreach (var name in classes)
            {
                builder.Append('.').Append(name).Append('\n');
            }
        }

        if (properties is not null)
        {
            foreach (var (key, value) in properties)
            {
                builder.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static ReadOnlySpan<char> GetLine(string source, int offset)
    {
        var start = Math.Clamp(offset, 0, source.Length);
        var length = source.AsSpan(start).IndexOfAny('\r', '\n');
        return length < 0 ? source.AsSpan(start) : source.AsSpan(start, length);
    }

    private static bool IsSameBlock(
        MarkdownPage before,
        MarkdownBlock oldBlock,
        MarkdownPage after,
        MarkdownBlock newBlock,
        MarkdownLineEdit edit,
        bool isOwnerOrAroundIt)
    {
        if (oldBlock.Kind != newBlock.Kind ||
            oldBlock.FenceLength != newBlock.FenceLength ||
            oldBlock.HasClosingLine != newBlock.HasClosingLine ||
            !string.Equals(oldBlock.ConfigurationJson, newBlock.ConfigurationJson, StringComparison.Ordinal) ||
            !GetHeadLine(before, oldBlock).SequenceEqual(GetHeadLine(after, newBlock)))
        {
            return false;
        }

        var delta = edit.InsertedLineCount - edit.RemovedLineCount;
        var removedEnd = edit.Line + edit.RemovedLineCount;

        // After the change: the block moved as a whole.
        if (oldBlock.Line >= edit.Line)
        {
            return newBlock.Line == oldBlock.Line + delta && newBlock.EndLine == oldBlock.EndLine + delta;
        }

        if (newBlock.Line != oldBlock.Line)
        {
            return false;
        }

        // Ended before the change. Only the block that was inserted into, and a block around it, can go on
        // through the new lines, and only when no closing line ends it.
        if (oldBlock.EndLine < edit.Line)
        {
            return newBlock.EndLine == oldBlock.EndLine ||
                   (isOwnerOrAroundIt && !oldBlock.HasClosingLine && newBlock.EndLine == edit.Line + edit.InsertedLineCount - 1);
        }

        // Around the change, with its last line after it.
        if (oldBlock.EndLine >= removedEnd)
        {
            return newBlock.EndLine == oldBlock.EndLine + delta;
        }

        // Around a removal that took its last line of content, so it now ends somewhere before the removed lines.
        return newBlock.EndLine >= oldBlock.Line && newBlock.EndLine < edit.Line;
    }

    private static ReadOnlySpan<char> GetHeadLine(MarkdownPage page, MarkdownBlock block) =>
        page.Source.AsSpan(block.HeadSpan.Start, block.HeadSpan.Length);

    // Every block of every kind with the block around it, in the order in which the parser lists them. A style
    // block is in no index of the page, and it can take over text like any other block.
    private static List<(MarkdownBlock Block, MarkdownBlock? Parent)> Flatten(IReadOnlyList<MarkdownBlock> blocks)
    {
        var result = new List<(MarkdownBlock Block, MarkdownBlock? Parent)>();
        Add(blocks, null);
        return result;

        void Add(IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? parent)
        {
            foreach (var block in siblings)
            {
                result.Add((block, parent));
                Add(block.Children, block);
            }
        }
    }

    // The block itself and every block around it.
    private static HashSet<MarkdownBlock> FindBlocksAround(
        List<(MarkdownBlock Block, MarkdownBlock? Parent)> blocks, MarkdownBlock? target)
    {
        var result = new HashSet<MarkdownBlock>(ReferenceEqualityComparer.Instance);
        if (target is null)
        {
            return result;
        }

        var parents = new Dictionary<MarkdownBlock, MarkdownBlock?>(ReferenceEqualityComparer.Instance);
        foreach (var (block, parent) in blocks)
        {
            parents[block] = parent;
        }

        for (var current = target; current is not null; current = parents.GetValueOrDefault(current))
        {
            result.Add(current);
        }

        return result;
    }
}

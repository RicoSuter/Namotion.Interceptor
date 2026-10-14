namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Maps the blocks of a page to the blocks of the page after one change of its lines, and checks that the
/// change did nothing to the blocks that it did not insert or remove.
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
    /// Null when the change did more than insert or remove its blocks. Every other block, of every kind, must
    /// have the same kind, fence, head line, closing line and json block as before, the lines that the change
    /// explains, and the same block around it. The first inserted block must be a slot block directly inside
    /// <paramref name="owner"/>, and every inserted block must end on the inserted lines.
    /// </returns>
    public static int[]? TryAlign(
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

        return TakeInsertedBlocks() && newPosition == newBlocks.Count && insertedBlocks.Count == expectedInsertedBlockCount
            ? map
            : null;
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

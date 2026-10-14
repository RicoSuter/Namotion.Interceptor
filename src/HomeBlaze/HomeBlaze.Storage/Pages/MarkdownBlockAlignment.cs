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
    /// <param name="expectedInsertedBlockCount">How many blocks the inserted lines must hold.</param>
    /// <param name="owner">The block of <paramref name="before"/> that the lines were inserted into, or null for a removal.</param>
    /// <returns>
    /// Null when a block that the change did not touch is not the same block afterwards: another kind, fence,
    /// head line or closing line, other lines than the change explains, or another number of inserted blocks.
    /// </returns>
    public static int[]? TryAlign(
        MarkdownPage before, MarkdownPage after, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner)
    {
        var removedEnd = edit.Line + edit.RemovedLineCount;
        var insertedEnd = edit.Line + edit.InsertedLineCount;
        HashSet<MarkdownBlock> blocksAroundOwner = owner is null ? [] : FindBlocksAround(before.Blocks, owner);

        var map = new int[before.AllBlocks.Count];
        var newIndex = 0;
        var insertedBlockCount = 0;

        void SkipInsertedBlocks()
        {
            while (newIndex < after.AllBlocks.Count &&
                   after.AllBlocks[newIndex].Line >= edit.Line &&
                   after.AllBlocks[newIndex].Line < insertedEnd)
            {
                newIndex++;
                insertedBlockCount++;
            }
        }

        for (var oldIndex = 0; oldIndex < map.Length; oldIndex++)
        {
            var oldBlock = before.AllBlocks[oldIndex];
            if (oldBlock.Line >= edit.Line && oldBlock.Line < removedEnd)
            {
                map[oldIndex] = -1;
                continue;
            }

            SkipInsertedBlocks();
            if (newIndex == after.AllBlocks.Count ||
                !IsSameBlock(before, oldBlock, after, after.AllBlocks[newIndex], edit, blocksAroundOwner.Contains(oldBlock)))
            {
                return null;
            }

            map[oldIndex] = newIndex++;
        }

        SkipInsertedBlocks();
        return newIndex == after.AllBlocks.Count && insertedBlockCount == expectedInsertedBlockCount ? map : null;
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

    // The block itself and every block around it.
    private static HashSet<MarkdownBlock> FindBlocksAround(IReadOnlyList<MarkdownBlock> blocks, MarkdownBlock target)
    {
        var path = new HashSet<MarkdownBlock>();
        Find(blocks);
        return path;

        bool Find(IReadOnlyList<MarkdownBlock> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(candidate, target) || Find(candidate.Children))
                {
                    path.Add(candidate);
                    return true;
                }
            }

            return false;
        }
    }
}

using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What a change of the blocks of a page did.</summary>
/// <param name="Write">The page with the new text and the subjects of its blocks.</param>
/// <param name="ChangedBlock">
/// The block of the page before the change whose content changed, or null when the change is directly on the page.
/// </param>
/// <param name="ResultBlockIndex">The index in the page of <paramref name="Write"/> of the block that the change moved or inserted, or -1.</param>
internal sealed record MarkdownStructureChange(MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1);

/// <summary>
/// Removes, swaps and inserts whole blocks in the text of a page. Every change is checked with
/// <see cref="MarkdownBlockAlignment"/>, so that it does nothing to the blocks and the text it did not mean to change.
/// </summary>
internal static class MarkdownPageOperations
{
    /// <summary>Removes the block with everything in it.</summary>
    /// <returns>Null with a refusal when the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryRemove(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var source = MarkdownBlockWriter.RemoveBlock(page.Source, block, out var edit);
        var after = MarkdownPageParser.Parse(source);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 0, owner: null) is not { } map)
        {
            refusal = $"Removing the block at line {block.Line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true), parent);
    }

    /// <summary>
    /// Swaps the block with the block next to it in <paramref name="direction"/> (-1 for up, 1 for down). The text
    /// between the two stays where it is.
    /// </summary>
    /// <returns>Null with a refusal when there is no such block or the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryMove(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock block, int direction, out string? refusal)
    {
        if (FindMoveTarget(page, block, direction) is not { } neighbor)
        {
            refusal = $"The block at line {block.Line} has no block of the same slot {(direction < 0 ? "before" : "after")} it.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var (first, second) = direction < 0 ? (neighbor, block) : (block, neighbor);
        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, first.HeadSpan.End);
        var start = MarkdownBlockWriter.GetLineStart(source, first.Line);
        var firstEnd = MarkdownBlockWriter.GetLineStart(source, first.EndLine + 1);
        var secondStart = MarkdownBlockWriter.GetLineStart(source, second.Line);
        var end = MarkdownBlockWriter.GetLineStart(source, second.EndLine + 1);

        var firstText = source[start..firstEnd];
        var middle = source[firstEnd..secondStart];
        var secondText = source[secondStart..end];

        // The second block can end the text without a line ending. It moves in front, so it gets one, and the first
        // block that ends the text now gives up its own.
        if (!secondText.EndsWith('\n'))
        {
            secondText += lineEnding;
            firstText = firstText.TrimEnd('\n').TrimEnd('\r');
        }

        var newSource = string.Concat(source[..start], secondText, middle, firstText, source[end..]);
        var lineCount = second.EndLine - first.Line + 1;
        var edit = new MarkdownLineEdit(first.Line, lineCount, lineCount);
        var after = MarkdownPageParser.Parse(newSource);
        var map = MarkdownBlockAlignment.TryAlign(
            page, after, edit, CountBlocks(first) + CountBlocks(second), parent, block.Kind);

        // The alignment skips the swapped lines, so a block without a closing line could have taken the text between.
        var secondLineCountAfter = second.EndLine - second.Line + 1;
        var middleLineCountAfter = second.Line - first.EndLine - 1;
        var movedSecondLine = first.Line + secondLineCountAfter + middleLineCountAfter;
        if (map is null ||
            !EndsAt(after, first.Line, first.Line + secondLineCountAfter - 1) ||
            !EndsAt(after, movedSecondLine, movedSecondLine + (first.EndLine - first.Line)))
        {
            refusal = $"Moving the block at line {block.Line} would change other blocks of the page.";
            return null;
        }

        // The swapped blocks are new to the alignment. Their text is the same, so their blocks are in the same order.
        var subjects = MapSubjects(blockSubjects, map);
        var secondLineCount = second.EndLine - second.Line + 1;
        var middleLineCount = second.Line - first.EndLine - 1;
        var secondAfter = FindBlockAt(after, first.Line);
        var firstAfter = FindBlockAt(after, first.Line + secondLineCount + middleLineCount);
        CarrySubjects(blockSubjects, second, secondAfter, subjects);
        CarrySubjects(blockSubjects, first, firstAfter, subjects);

        var moved = ReferenceEquals(block, second) ? secondAfter : firstAfter;
        return new MarkdownStructureChange(new MarkdownBlockWrite(after, subjects, [], HasInsertedOrRemovedBlocks: true), parent, moved.Index);
    }

    /// <summary>
    /// Gets the block that <see cref="TryMove"/> swaps the block with: the block right next to it among the blocks of
    /// the same parent, when it is of the same kind and, for a slot block, of the same slot. Otherwise null.
    /// </summary>
    public static MarkdownBlock? FindMoveTarget(MarkdownPage page, MarkdownBlock block, int direction)
    {
        if (direction is not (-1 or 1) ||
            block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) ||
            FindSiblings(page, block, out _) is not { } siblings)
        {
            return null;
        }

        var position = IndexOf(siblings, block) + direction;
        if (position < 0 || position >= siblings.Count)
        {
            return null;
        }

        var neighbor = siblings[position];
        return neighbor.Kind == block.Kind &&
               (block.Kind == MarkdownBlockKind.Widget || neighbor.Head!.Name == block.Head!.Name)
            ? neighbor
            : null;
    }

    /// <summary>Counts the block and every block inside it, of every kind.</summary>
    public static int CountBlocks(MarkdownBlock block)
    {
        var count = 1;
        foreach (var child in block.Children)
        {
            count += CountBlocks(child);
        }

        return count;
    }

    // The blocks beside the block, with the block around them, which is null directly on the page.
    internal static IReadOnlyList<MarkdownBlock>? FindSiblings(MarkdownPage page, MarkdownBlock block, out MarkdownBlock? parent)
    {
        parent = null;
        return Find(page.Blocks, null, block, ref parent);

        static IReadOnlyList<MarkdownBlock>? Find(
            IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? around, MarkdownBlock target, ref MarkdownBlock? parent)
        {
            foreach (var sibling in siblings)
            {
                if (ReferenceEquals(sibling, target))
                {
                    parent = around;
                    return siblings;
                }

                if (Find(sibling.Children, sibling, target, ref parent) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    // Lines are counted by line feeds, and Markdig ends a line at a lone carriage return as well.
    private static bool CanInsertOrRemoveBlocks(MarkdownPage page, out string? refusal)
    {
        refusal = MarkdownBlockWriter.HasLoneCarriageReturn(page.Source)
            ? "Blocks cannot be inserted or removed on a page with a line ending that is a carriage return alone."
            : null;
        return refusal is null;
    }

    private static Dictionary<int, IInterceptorSubject> MapSubjects(IReadOnlyDictionary<int, IInterceptorSubject> subjects, int[] map)
    {
        var result = new Dictionary<int, IInterceptorSubject>(subjects.Count);
        foreach (var (index, subject) in subjects)
        {
            if (map[index] >= 0)
            {
                result[map[index]] = subject;
            }
        }

        return result;
    }

    private static void CarrySubjects(
        IReadOnlyDictionary<int, IInterceptorSubject> oldSubjects, MarkdownBlock oldBlock, MarkdownBlock newBlock, Dictionary<int, IInterceptorSubject> newSubjects)
    {
        var oldIndices = CollectIndices(oldBlock);
        var newIndices = CollectIndices(newBlock);
        for (var position = 0; position < oldIndices.Count && position < newIndices.Count; position++)
        {
            if (oldSubjects.TryGetValue(oldIndices[position], out var subject))
            {
                newSubjects[newIndices[position]] = subject;
            }
        }
    }

    // The indices of the block and of the blocks inside it in document order. A style block has none.
    private static List<int> CollectIndices(MarkdownBlock block)
    {
        var result = new List<int>();
        Add(block);
        return result;

        void Add(MarkdownBlock current)
        {
            if (current.Index >= 0)
            {
                result.Add(current.Index);
            }

            foreach (var child in current.Children)
            {
                Add(child);
            }
        }
    }

    private static bool EndsAt(MarkdownPage page, int line, int endLine) => FindBlockAt(page, line).EndLine == endLine;

    // The first block on the inserted lines, which is the new block: blocks are listed in the order of their head lines.
    private static MarkdownBlock? FindFirstBlockFrom(MarkdownPage page, MarkdownLineEdit edit)
    {
        foreach (var block in page.AllBlocks)
        {
            if (block.Line >= edit.Line && block.Line < edit.InsertedEnd)
            {
                return block;
            }
        }

        return null;
    }

    private static MarkdownBlock FindBlockAt(MarkdownPage page, int line)
    {
        foreach (var block in page.AllBlocks)
        {
            if (block.Line == line)
            {
                return block;
            }
        }

        throw new InvalidOperationException($"The page has no block at line {line}.");
    }

    private static int IndexOf(IReadOnlyList<MarkdownBlock> blocks, MarkdownBlock block)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (ReferenceEquals(blocks[index], block))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Inserts a new widget block into the content of <paramref name="owner"/>, or of the page when it is null: before
    /// <paramref name="before"/>, or after the content when it is null, which is before the first slot block of the
    /// owner. The block takes the fence of the block beside it and closing lines unless the page has none.
    /// </summary>
    public static MarkdownStructureChange? TryInsertWidget(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock? owner,
        MarkdownBlock? before,
        string typeName,
        string? id,
        string configurationJson,
        out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var source = page.Source;
        var siblings = owner?.Children ?? page.Blocks;
        var firstSlotBlock = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Slot);
        var line = before?.Line
            ?? firstSlotBlock?.Line
            ?? (owner is null ? MarkdownBlockWriter.GetAppendLine(source) : owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1);
        line = page.GetLineForNewBlock(line);

        var reference = before ?? siblings.LastOrDefault(block => block.Kind == MarkdownBlockKind.Widget);
        var fenceLength = reference?.FenceLength ?? (owner is null ? MarkdownBlockFence.MinimumLength : owner.FenceLength + 1);
        var indentation = reference is not null ? MarkdownBlockWriter.GetIndentation(source, reference)
            : owner is not null ? MarkdownBlockWriter.GetIndentation(source, owner)
            : string.Empty;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, reference?.HeadSpan.End ?? owner?.HeadSpan.End ?? 0);
        var hasClosingLine = !page.AllBlocks.Any(
            block => block.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot && !block.HasClosingLine);

        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(typeName, id, configurationJson, fenceLength, lineEnding, hasClosingLine),
            indentation,
            lineEnding);
        var newSource = MarkdownBlockWriter.InsertBlockText(source, line, text, lineEnding, out var edit);
        var after = MarkdownPageParser.Parse(newSource);
        var map = MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 1, owner, MarkdownBlockKind.Widget);
        if (map is null)
        {
            refusal = $"Inserting a block at line {line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true),
            owner,
            FindFirstBlockFrom(after, edit)?.Index ?? -1);
    }

    /// <summary>
    /// Appends a new block of the slot <paramref name="slotName"/> to <paramref name="owner"/>, after the last block of
    /// that slot and written like it, as <see cref="MarkdownBlockWriter.GetSlotBlockPlacement"/> places it.
    /// </summary>
    public static MarkdownStructureChange? TryAppendSlotBlock(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock owner,
        string slotName,
        string configurationJson,
        out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, owner, slotName);
        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(
                slotName, null, configurationJson, placement.FenceLength, placement.LineEnding, placement.HasClosingLine),
            placement.Indentation,
            placement.LineEnding);
        var source = MarkdownBlockWriter.InsertLines(page.Source, placement.Line, text, placement.LineEnding, out var edit);
        var after = MarkdownPageParser.Parse(source);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 1, owner) is not { } map)
        {
            refusal = $"Inserting a '{slotName}' block at line {placement.Line} would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true),
            owner,
            FindFirstBlockFrom(after, edit)?.Index ?? -1);
    }

    /// <summary>
    /// Gets the block whose content <paramref name="fragment"/> shows: the block of the subject that holds it in its
    /// default slot, or the slot block that holds it for a named or a list slot. Null when no block of the page holds it.
    /// </summary>
    public static MarkdownBlock? FindContentBlock(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject fragment)
    {
        foreach (var (index, subject) in blockSubjects)
        {
            foreach (var slot in SlotMap.For(subject.GetType()).Slots)
            {
                if (!slot.IsOpen)
                {
                    continue;
                }

                var value = slot.GetValue(subject);
                var position = value is Array values ? Array.IndexOf(values, fragment) : ReferenceEquals(value, fragment) ? 0 : -1;
                if (position < 0)
                {
                    continue;
                }

                var block = page.AllBlocks[index];
                return slot.IsDefault && !slot.IsList
                    ? block
                    : MarkdownBlockWriter.GetSlotBlocks(block, slot.Name).ElementAtOrDefault(position);
            }
        }

        return null;
    }
}

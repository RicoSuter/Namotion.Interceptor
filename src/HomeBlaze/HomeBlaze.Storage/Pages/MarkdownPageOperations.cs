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
/// Removes, moves and inserts whole blocks in the text of a page. Every change is checked with
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
    /// Moves the block one place in <paramref name="direction"/> (-1 for up, 1 for down): it is cut out and inserted before the
    /// item before it, or after the item after it. An item is a widget block or a text block of the content of the block, or
    /// for a slot block a block of the same slot. The text around the block stays where it is.
    /// </summary>
    /// <returns>Null with a refusal when there is no item in that direction or the change would change other blocks of the page.</returns>
    public static MarkdownStructureChange? TryMove(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock block,
        int direction,
        out string? refusal)
    {
        if (FindMoveLine(page, block, direction) is not { } targetLine)
        {
            refusal = $"The block at line {block.Line} has nothing {(direction < 0 ? "before" : "after")} it to move past.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, block.HeadSpan.End);
        var blockText = MarkdownBlockWriter.GetLines(source, block.Line, block.EndLine);
        var standsApart = MarkdownBlockWriter.HasEmptyLineAround(source, block.Line, block.EndLine);

        // Each step is aligned on its own, so that together they change nothing outside the cut and the inserted lines.
        var cutSource = MarkdownBlockWriter.CutLines(source, block.Line, block.EndLine, out var cut);
        var cutPage = MarkdownPageParser.Parse(cutSource);
        var cutMap = MarkdownBlockAlignment.TryAlign(page, cutPage, cut, expectedInsertedBlockCount: 0, owner: null);
        if (cutMap is null)
        {
            refusal = FormatMoveRefusal(block);
            return null;
        }

        var line = targetLine >= cut.RemovedEnd ? targetLine - cut.RemovedLineCount + cut.InsertedLineCount : targetLine;
        var newSource = MarkdownBlockWriter.InsertMovedLines(cutSource, line, blockText, lineEnding, standsApart, out var insert);
        var after = MarkdownPageParser.Parse(newSource);
        var cutParent = parent is null ? null : cutPage.AllBlocks[cutMap[parent.Index]];
        if (MarkdownBlockAlignment.TryAlign(cutPage, after, insert, CountBlocks(block), cutParent, block.Kind) is not { } insertMap ||
            FindFirstBlockFrom(after, insert) is not { } moved)
        {
            refusal = FormatMoveRefusal(block);
            return null;
        }

        var subjects = new Dictionary<int, IInterceptorSubject>(blockSubjects.Count);
        foreach (var (index, subject) in blockSubjects)
        {
            var cutIndex = cutMap[index];
            if (cutIndex >= 0 && insertMap[cutIndex] >= 0)
            {
                subjects[insertMap[cutIndex]] = subject;
            }
        }

        // The moved blocks are new to the alignment. Their text is the same, so their blocks are in the same order.
        CarrySubjects(blockSubjects, block, moved, subjects);
        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, subjects, [], HasInsertedOrRemovedBlocks: true), parent, moved.Index);
    }

    private static string FormatMoveRefusal(MarkdownBlock block) =>
        $"Moving the block at line {block.Line} would change other blocks of the page.";

    /// <summary>
    /// Gets the line of the page before the move before which <see cref="TryMove"/> inserts the block: the first line of the
    /// item before it, or the line after the item after it. Null when the block has no such item.
    /// </summary>
    public static int? FindMoveLine(MarkdownPage page, MarkdownBlock block, int direction) =>
        direction is -1 or 1 && GetMoveItems(page, block, out var position) is { } items
            ? FindMoveLine(items, position, direction)
            : null;

    /// <summary>
    /// Gets whether <see cref="TryMove"/> can move the block up and down: whether
    /// <see cref="FindMoveLine(MarkdownPage, MarkdownBlock, int)"/> finds a line in each direction.
    /// </summary>
    public static (bool CanMoveUp, bool CanMoveDown) GetMoveDirections(MarkdownPage page, MarkdownBlock block) =>
        GetMoveItems(page, block, out var position) is { } items
            ? (FindMoveLine(items, position, -1) is not null, FindMoveLine(items, position, 1) is not null)
            : default;

    private static int? FindMoveLine(List<MoveItem> items, int position, int direction)
    {
        var index = position + direction;
        if (index < 0 || index >= items.Count)
        {
            return null;
        }

        var item = items[index];
        return direction < 0 ? item.Line : item.EndLine + 1;
    }

    // A place that a moved block passes: a block, or text without a block, with its lines.
    private readonly record struct MoveItem(int Line, int EndLine, MarkdownBlock? Block);

    // The items among which the block moves, with its position among them. Null when it is no widget or slot block.
    private static List<MoveItem>? GetMoveItems(MarkdownPage page, MarkdownBlock block, out int position)
    {
        position = -1;
        if (block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) ||
            FindSiblings(page, block, out var parent) is not { } siblings)
        {
            return null;
        }

        var items = block.Kind == MarkdownBlockKind.Slot ? GetSlotItems(siblings, block.Head!.Name) : GetContentItems(page, siblings, parent, block);
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index].Block, block))
            {
                position = index;
                return items;
            }
        }

        return null;
    }

    private static List<MoveItem> GetSlotItems(IReadOnlyList<MarkdownBlock> siblings, string slotName)
    {
        var items = new List<MoveItem>();
        foreach (var sibling in siblings)
        {
            if (sibling.Kind == MarkdownBlockKind.Slot && sibling.Head!.Name == slotName)
            {
                items.Add(new MoveItem(sibling.Line, sibling.EndLine, sibling));
            }
        }

        return items;
    }

    // The widget blocks of the content and its text blocks, which also stand for its style and error blocks. Only those
    // between the slot blocks around the block count, so that a widget never passes a slot block of its owner.
    private static List<MoveItem> GetContentItems(
        MarkdownPage page, IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? owner, MarkdownBlock block)
    {
        var firstLine = 0;
        var lastLine = int.MaxValue;
        foreach (var sibling in siblings)
        {
            if (sibling.Kind != MarkdownBlockKind.Slot)
            {
                continue;
            }

            if (sibling.Line < block.Line)
            {
                firstLine = sibling.EndLine + 1;
            }
            else
            {
                lastLine = sibling.Line - 1;
                break;
            }
        }

        var items = new List<MoveItem>();
        foreach (var sibling in siblings)
        {
            if (sibling.Kind == MarkdownBlockKind.Widget && sibling.Line >= firstLine && sibling.Line <= lastLine)
            {
                items.Add(new MoveItem(sibling.Line, sibling.EndLine, sibling));
            }
        }

        foreach (var textBlock in MarkdownTextBlocks.Find(page))
        {
            if (ReferenceEquals(textBlock.Owner, owner) && textBlock.Line >= firstLine && textBlock.Line <= lastLine)
            {
                items.Add(new MoveItem(textBlock.Line, textBlock.EndLine, null));
            }
        }

        items.Sort(static (first, second) => first.Line.CompareTo(second.Line));
        return items;
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

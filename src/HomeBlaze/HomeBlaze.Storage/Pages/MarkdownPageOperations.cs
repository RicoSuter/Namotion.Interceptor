using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What a change of the blocks of a page did.</summary>
/// <param name="Write">The page with the new text and the subjects of its blocks.</param>
/// <param name="ChangedBlock">
/// The block of the page before the change whose content changed, or null when the change is directly on the page.
/// </param>
/// <param name="ResultBlockIndex">The index in the page of <paramref name="Write"/> of the block that the change moved or inserted, or -1.</param>
/// <param name="ResultTextLine">The first line in the page of <paramref name="Write"/> of the text that the change wrote, or 0.</param>
internal sealed record MarkdownStructureChange(
    MarkdownBlockWrite Write, MarkdownBlock? ChangedBlock, int ResultBlockIndex = -1, int ResultTextLine = 0);

/// <summary>
/// Inserts new content into the content of <paramref name="container"/> before the 1-based <paramref name="line"/> from
/// <see cref="MarkdownPageOperations.GetInsertLine"/>, as <see cref="MarkdownPageOperations.TryInsertWidget"/> and
/// <see cref="MarkdownPageOperations.TryInsertText"/> do.
/// </summary>
internal delegate MarkdownStructureChange? MarkdownContentInsert(
    MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock container, int line, out string? refusal);

/// <summary>
/// Removes, moves and inserts whole blocks in the text of a page. Every change is checked with
/// <see cref="MarkdownBlockAlignment"/>, so that it does nothing to the blocks and the text it did not mean to change.
/// </summary>
internal static class MarkdownPageOperations
{
    private const string WidgetPartRefusal = "Text can hold widgets, not the parts of a widget.";
    private const string StyleBlockRefusal = "Text can hold widgets, not style blocks.";
    private const string NoClosingLineRefusal = "Add the closing line of the block first.";
    private const string MoveRefusal = "Moving the block would change other blocks of the page.";
    private const string TextMoveRefusal = "Moving the text would change other blocks of the page.";
    private const string OpenHtmlRefusal = "The text would leave HTML open over the text after it.";

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
            refusal = "Removing the block would change other blocks of the page.";
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
        if (direction is not (-1 or 1) ||
            GetMoveItems(page, block, out var position) is not { } items ||
            FindMoveLine(items, position, direction) is not { } targetLine)
        {
            refusal = direction < 0 ? "The block has nothing before it to move past." : "The block has nothing after it to move past.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        FindSiblings(page, block, out var parent);
        var lineEnding = MarkdownBlockWriter.GetLineEnding(page.Source, block.HeadSpan.End);
        if (TryMoveLines(page, items[position].Line, block.EndLine, targetLine, lineEnding, parent, CountBlocks(block), block.Kind, out var insert) is not { } moved ||
            FindFirstBlockFrom(moved.After, insert) is not { } movedBlock)
        {
            refusal = MoveRefusal;
            return null;
        }

        var subjects = ComposeSubjects(blockSubjects, moved.CutMap, moved.InsertMap);

        // The moved blocks are new to the alignment. Their text is the same, so their blocks are in the same order.
        CarrySubjects(blockSubjects, block, movedBlock, subjects);
        return new MarkdownStructureChange(
            new MarkdownBlockWrite(moved.After, subjects, [], HasInsertedOrRemovedBlocks: true), parent, movedBlock.Index);
    }

    // Cuts the lines and inserts them before `targetLine` of the page before the move. Each step is aligned on its own, so
    // that together they change nothing outside the cut and the inserted lines. Null when a step changes other blocks.
    private static (MarkdownPage After, int[] CutMap, int[] InsertMap)? TryMoveLines(
        MarkdownPage page,
        int line,
        int endLine,
        int targetLine,
        string lineEnding,
        MarkdownBlock? owner,
        int insertedBlockCount,
        MarkdownBlockKind insertedKind,
        out MarkdownLineEdit insert)
    {
        var source = page.Source;
        var lines = MarkdownBlockWriter.GetLines(source, line, endLine);
        var standsApart = MarkdownBlockWriter.HasEmptyLineAround(source, line, endLine);
        var cutSource = MarkdownBlockWriter.CutLines(source, line, endLine, out var cut);
        var cutPage = MarkdownPageParser.Parse(cutSource);
        if (MarkdownBlockAlignment.TryAlign(page, cutPage, cut, expectedInsertedBlockCount: 0, owner: null) is not { } cutMap)
        {
            insert = default;
            return null;
        }

        var insertLine = targetLine >= cut.RemovedEnd ? targetLine - cut.RemovedLineCount + cut.InsertedLineCount : targetLine;
        var after = MarkdownPageParser.Parse(
            MarkdownBlockWriter.InsertMovedLines(cutSource, insertLine, lines, lineEnding, standsApart, out insert));
        var cutOwner = owner is null ? null : cutPage.AllBlocks[cutMap[owner.Index]];
        return MarkdownBlockAlignment.TryAlign(cutPage, after, insert, insertedBlockCount, cutOwner, insertedKind) is { } insertMap
            ? (after, cutMap, insertMap)
            : null;
    }

    /// <summary>
    /// Moves the text block one place in <paramref name="direction"/> (-1 for up, 1 for down) among the items of its content,
    /// as <see cref="TryMove"/> moves a widget block: it is cut out and inserted before the item before it, or after the item
    /// after it, where an item is a widget block or a text block between the slot blocks around the text block.
    /// </summary>
    /// <returns>
    /// Null with a refusal when the text block cannot be edited, has no item in that direction, would go past a widget block
    /// next to raw HTML or where a block without a closing line takes it, or the change would change other blocks of the page or join the moved
    /// text with the text around it.
    /// </returns>
    public static MarkdownStructureChange? TryMoveText(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownTextBlock textBlock,
        int direction,
        out string? refusal)
    {
        if (!CanEditText(textBlock, out refusal))
        {
            return null;
        }

        if (direction is not (-1 or 1) ||
            GetTextMoveItems(page, textBlock, out var position) is not { } items ||
            FindTextMoveLine(items, position, direction, out var joinsHtml) is not { } targetLine)
        {
            refusal = direction < 0 ? "The text has nothing before it to move past." : "The text has nothing after it to move past.";
            return null;
        }

        if (joinsHtml)
        {
            refusal = "The text would join the HTML next to it.";
            return null;
        }

        var owner = textBlock.Owner;
        if (IsAfterOpenHtml(page, owner, targetLine))
        {
            refusal = "The text would join the HTML next to it.";
            return null;
        }

        if (!CanInsertTextAt(page, owner, targetLine))
        {
            refusal = "Moving the text would put it into a block without a closing line.";
            return null;
        }

        // HTML that shows nothing before the text, such as a comment that names it, moves with it.
        var line = items[position].Line;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(page.Source, MarkdownBlockWriter.GetLineStart(page.Source, line));
        if (TryMoveLines(page, line, textBlock.EndLine, targetLine, lineEnding, owner, 0, MarkdownBlockKind.Slot, out var insert) is not { } moved)
        {
            refusal = TextMoveRefusal;
            return null;
        }

        var movedLine = FindFirstTextLine(moved.After, insert) + textBlock.Line - line;
        if (!IsSameText(page, textBlock, moved.After, movedLine, insert))
        {
            refusal = "The text would join the text around it.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(moved.After, ComposeSubjects(blockSubjects, moved.CutMap, moved.InsertMap), [], HasInsertedOrRemovedBlocks: false),
            owner,
            ResultTextLine: movedLine);
    }

    // The comparison of the document skips the inserted lines, so the moved text is checked on its own: one text block that
    // can be edited, on the inserted lines, with the same Markdown. A container before it that took it, such as a loose list
    // or a footnote, starts before the inserted lines.
    private static bool IsSameText(MarkdownPage page, MarkdownTextBlock textBlock, MarkdownPage after, int movedLine, MarkdownLineEdit insert)
    {
        foreach (var candidate in MarkdownTextBlocks.Find(after))
        {
            if (candidate.Line == movedLine)
            {
                return candidate.IsEditable &&
                       candidate.EndLine < insert.InsertedEnd &&
                       GetText(after, candidate) == GetText(page, textBlock);
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether <see cref="TryMoveText"/> can move the text block up and down: whether it can be edited, has an item in
    /// that direction, and neither raw HTML nor a block without a closing line would take it there.
    /// </summary>
    public static (bool CanMoveUp, bool CanMoveDown) GetTextMoveDirections(MarkdownPage page, MarkdownTextBlock textBlock)
    {
        if (!CanEditText(textBlock, out _) || GetTextMoveItems(page, textBlock, out var position) is not { } items)
        {
            return default;
        }

        return (CanMoveTextTo(page, textBlock, items, position, -1), CanMoveTextTo(page, textBlock, items, position, 1));
    }

    private static bool CanMoveTextTo(MarkdownPage page, MarkdownTextBlock textBlock, List<MoveItem> items, int position, int direction) =>
        FindTextMoveLine(items, position, direction, out var joinsHtml) is { } targetLine &&
        !joinsHtml &&
        CanInsertTextAt(page, textBlock.Owner, targetLine);

    // Past a widget block, the text comes next to the text beyond it, and raw HTML there can take it, such as an element that
    // the HTML leaves open up to the widget block. Past raw HTML in its own run, the text goes beyond the HTML that closes what
    // it opens, or into HTML that leaves something open up to the end of the run, which IsAfterOpenHtml finds.
    private static int? FindTextMoveLine(List<MoveItem> items, int position, int direction, out bool joinsHtml)
    {
        var line = FindMoveLine(items, position, direction);
        var beyond = position + 2 * direction;
        joinsHtml = line is not null &&
                    items[position + direction].Block is not null &&
                    beyond >= 0 && beyond < items.Count &&
                    items[beyond].HoldsHtml;
        return line;
    }

    // The items among which the text block moves, with its position among them.
    private static List<MoveItem>? GetTextMoveItems(MarkdownPage page, MarkdownTextBlock textBlock, out int position)
    {
        var items = GetContentItems(page, textBlock.Owner?.Children ?? page.Blocks, textBlock.Owner, textBlock.Line);
        for (position = 0; position < items.Count; position++)
        {
            if (items[position].Block is null && items[position].OwnLine == textBlock.Line)
            {
                return items;
            }
        }

        position = -1;
        return null;
    }

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

    // A place that a moved block passes: a block, or text without a block, with its lines and whether it holds raw HTML. Its
    // first line is OwnLine, or the line of HTML that shows nothing before it and moves with it.
    private readonly record struct MoveItem(int Line, int EndLine, MarkdownBlock? Block, bool HoldsHtml = false, bool ShowsNothing = false)
    {
        public int OwnLine { get; init; } = Line;
    }

    // The items among which the block moves, with its position among them. Null when it is no widget or slot block.
    private static List<MoveItem>? GetMoveItems(MarkdownPage page, MarkdownBlock block, out int position)
    {
        position = -1;
        if (block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) ||
            FindSiblings(page, block, out var parent) is not { } siblings)
        {
            return null;
        }

        var items = block.Kind == MarkdownBlockKind.Slot ? GetSlotItems(siblings, block.Head!.Name) : GetContentItems(page, siblings, parent, block.Line);
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
    // between the slot blocks around the item at `line` count, so that an item never passes a slot block of its owner.
    private static List<MoveItem> GetContentItems(
        MarkdownPage page, IReadOnlyList<MarkdownBlock> siblings, MarkdownBlock? owner, int line)
    {
        var firstLine = 0;
        var lastLine = int.MaxValue;
        foreach (var sibling in siblings)
        {
            if (sibling.Kind != MarkdownBlockKind.Slot)
            {
                continue;
            }

            if (sibling.Line < line)
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
                items.Add(new MoveItem(textBlock.Line, textBlock.EndLine, null, textBlock.HoldsHtml, textBlock.ShowsNothing));
            }
        }

        items.Sort(static (first, second) => first.Line.CompareTo(second.Line));

        // HTML that shows nothing, such as a comment that names the block after it, moves with the item after it, and a move
        // past it would change nothing that shows. At the end of the content it stays where it is.
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index].ShowsNothing)
            {
                if (index + 1 < items.Count)
                {
                    items[index + 1] = items[index + 1] with { Line = items[index].Line };
                }

                items.RemoveAt(index);
            }
        }

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

    // The subject of every block that both steps of a move kept, at its index after the second step.
    private static Dictionary<int, IInterceptorSubject> ComposeSubjects(
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, int[] firstMap, int[] secondMap)
    {
        var subjects = new Dictionary<int, IInterceptorSubject>(blockSubjects.Count);
        foreach (var (index, subject) in blockSubjects)
        {
            var firstIndex = firstMap[index];
            if (firstIndex >= 0 && secondMap[firstIndex] >= 0)
            {
                subjects[secondMap[firstIndex]] = subject;
            }
        }

        return subjects;
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
    /// Gets the line before which new content goes into the content of <paramref name="owner"/>, or of the page when it is
    /// null: <paramref name="beforeLine"/>, the first line of an item of that content, or else the end of the content, which
    /// is before the first slot block of the owner. Never before or inside the json block of the block around the line.
    /// </summary>
    public static int GetInsertLine(MarkdownPage page, MarkdownBlock? owner, int? beforeLine)
    {
        var siblings = owner?.Children ?? page.Blocks;
        var firstSlotBlock = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Slot);
        var line = beforeLine
            ?? firstSlotBlock?.Line
            ?? (owner is null ? MarkdownBlockWriter.GetAppendLine(page.Source) : owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1);
        return page.GetLineForNewBlock(line);
    }

    /// <summary>
    /// Inserts a new widget block into the content of <paramref name="owner"/>, or of the page when it is null, before the
    /// 1-based <paramref name="line"/> from <see cref="GetInsertLine"/>. The block takes the fence of the widget block at or
    /// after the line, or else of the last widget block, and closing lines unless the page has none.
    /// </summary>
    public static MarkdownStructureChange? TryInsertWidget(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock? owner,
        int line,
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
        var reference = siblings.FirstOrDefault(block => block.Kind == MarkdownBlockKind.Widget && block.Line >= line)
            ?? siblings.LastOrDefault(block => block.Kind == MarkdownBlockKind.Widget);
        var fenceLength = reference?.FenceLength ?? GetWidgetFenceLength(owner);
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
            refusal = "Inserting a block here would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true),
            owner,
            FindFirstBlockFrom(after, edit)?.Index ?? -1);
    }

    /// <summary>
    /// Gets how many fence characters a widget block needs at least to stand directly in the content of
    /// <paramref name="owner"/>, or of the page when it is null: one more than the owner, or the shortest fence on the page.
    /// </summary>
    public static int GetWidgetFenceLength(MarkdownBlock? owner) =>
        owner is null ? MarkdownBlockFence.MinimumLength : owner.FenceLength + 1;

    /// <summary>Gets the Markdown of a text block: its lines without the indentation of its content, with line feeds.</summary>
    public static string GetText(MarkdownPage page, MarkdownTextBlock textBlock)
    {
        var lines = MarkdownBlockWriter.GetLines(page.Source, textBlock.Line, textBlock.EndLine).ReplaceLineEndings("\n");
        var indentation = GetContentIndentation(page, textBlock.Owner);
        return indentation.Length == 0 ? lines : MarkdownBlockWriter.RemoveIndentation(lines, indentation);
    }

    /// <summary>
    /// Replaces the lines of the text block with <paramref name="text"/>, or removes them when it is empty or white space.
    /// The text gets the line endings of the page and the indentation of its content, and an empty line keeps it apart from
    /// a line of text before or after it. Complete widget blocks at the top level of the text become new blocks of the
    /// content.
    /// </summary>
    /// <returns>
    /// Null with a refusal when the text block cannot be edited, the text holds a block that is no widget block at its top
    /// level, a widget block with too short a fence, an invalid block or a name that a block of the page or of the text
    /// already has, or the change would change other blocks of the page or the text around it.
    /// </returns>
    public static MarkdownStructureChange? TryReplaceText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, string text, out string? refusal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TryRemoveText(page, blockSubjects, textBlock, out refusal);
        }

        if (!CanEditText(textBlock, out refusal))
        {
            return null;
        }

        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, MarkdownBlockWriter.GetLineStart(source, textBlock.Line));
        var prepared = PrepareText(page, text, textBlock.Owner, lineEnding);
        if (!TryCountWidgetBlocks(page, prepared, textBlock.Owner, out var blockCount, out refusal))
        {
            return null;
        }

        var newSource = MarkdownBlockWriter.ReplaceLines(source, textBlock.Line, textBlock.EndLine, prepared, lineEnding, out var edit);
        return AlignText(page, blockSubjects, newSource, edit, textBlock.Owner, blockCount, "Changing the text would change other blocks of the page.", out refusal);
    }

    /// <summary>Removes the lines of the text block so that the text around it stays apart as before.</summary>
    /// <returns>
    /// Null with a refusal when the text block cannot be edited or the change would change other blocks of the page or the
    /// text around it.
    /// </returns>
    public static MarkdownStructureChange? TryRemoveText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownTextBlock textBlock, out string? refusal)
    {
        if (!CanEditText(textBlock, out refusal))
        {
            return null;
        }

        var newSource = MarkdownBlockWriter.CutLines(page.Source, textBlock.Line, textBlock.EndLine, out var edit);
        var after = MarkdownPageParser.Parse(newSource);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 0, owner: null) is not { } map)
        {
            refusal = "Removing the text would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: false), textBlock.Owner);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> into the content of <paramref name="owner"/>, or of the page when it is null, before
    /// the 1-based <paramref name="line"/> from <see cref="GetInsertLine"/>, with empty lines around it like a block. Complete
    /// widget blocks at the top level of the text become new blocks of the content.
    /// </summary>
    /// <returns>
    /// Null with a refusal when the text is empty, holds a block that is no widget block at its top level, a widget block
    /// with too short a fence, an invalid block or a name that a block of the page or of the text already has, or the change
    /// would change other blocks of the page or the text around it.
    /// </returns>
    public static MarkdownStructureChange? TryInsertText(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, MarkdownBlock? owner, int line, string text, out string? refusal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            refusal = "The text is empty.";
            return null;
        }

        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        if (IsAfterOpenHtml(page, owner, line))
        {
            refusal = "The text would join the HTML before it.";
            return null;
        }

        var source = page.Source;
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, MarkdownBlockWriter.GetLineStart(source, Math.Max(line - 1, 1)));
        var prepared = PrepareText(page, text, owner, lineEnding);
        if (!TryCountWidgetBlocks(page, prepared, owner, out var blockCount, out refusal))
        {
            return null;
        }

        var newSource = MarkdownBlockWriter.InsertBlockText(source, line, prepared, lineEnding, out var edit);
        return AlignText(page, blockSubjects, newSource, edit, owner, blockCount, "Inserting the text here would change other blocks of the page.", out refusal);
    }

    /// <summary>
    /// Gets whether text can go into the content of <paramref name="owner"/>, or of the page when it is null, before the
    /// 1-based <paramref name="line"/>: not right after a block without a closing line, which would take the text.
    /// </summary>
    public static bool CanInsertTextAt(MarkdownPage page, MarkdownBlock? owner, int line)
    {
        MarkdownBlock? previous = null;
        foreach (var sibling in owner?.Children ?? page.Blocks)
        {
            if (sibling.Line < line)
            {
                previous = sibling;
            }
        }

        return previous is not { HasClosingLine: false } && !IsAfterOpenHtml(page, owner, line);
    }

    // Whether the content of `owner` has raw HTML before the line that leaves something open up to the end of its run, which
    // takes text that goes there. The HTML ends where a widget or slot block ends the run.
    private static bool IsAfterOpenHtml(MarkdownPage page, MarkdownBlock? owner, int line)
    {
        MarkdownTextBlock? last = null;
        foreach (var textBlock in MarkdownTextBlocks.Find(page))
        {
            if (textBlock.Line >= line)
            {
                break;
            }

            if (ReferenceEquals(textBlock.Owner, owner))
            {
                last = textBlock;
            }
        }

        if (last is not { LeavesHtmlOpen: true })
        {
            return false;
        }

        foreach (var sibling in owner?.Children ?? page.Blocks)
        {
            if (sibling.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot && sibling.Line > last.Line && sibling.Line < line)
            {
                return false;
            }
        }

        return true;
    }

    // A text block that cannot be edited can hold blocks, such as an invalid block in a list item, which the change would drop
    // without the alignment seeing it: the lines of the text block are all replaced or removed. No text block of a page with
    // a lone carriage return can be edited, see MarkdownTextBlocks.Find.
    private static bool CanEditText(MarkdownTextBlock textBlock, out string? refusal)
    {
        refusal = textBlock.IsEditable ? null : "The text cannot be edited as text.";
        return refusal is null;
    }

    // The new blocks of the text stand directly in the content and end on its lines, and every other block, as well as the
    // Markdown document around the text, stays as it was.
    private static MarkdownStructureChange? AlignText(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        string newSource,
        MarkdownLineEdit edit,
        MarkdownBlock? owner,
        int insertedBlockCount,
        string alignmentRefusal,
        out string? refusal)
    {
        var after = MarkdownPageParser.Parse(newSource);
        var textLine = FindFirstTextLine(after, edit);
        var takesTextAfter = false;
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, insertedBlockCount, owner, MarkdownBlockKind.Widget) is not { } map ||
            (textLine > 0 && !StandsApart(after, edit, textLine, out takesTextAfter)))
        {
            refusal = takesTextAfter ? OpenHtmlRefusal : alignmentRefusal;
            return null;
        }

        refusal = null;
        var hasNewBlocks = insertedBlockCount > 0;
        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: hasNewBlocks),
            owner,
            hasNewBlocks ? FindFirstBlockFrom(after, edit)?.Index ?? -1 : -1,
            textLine);
    }

    // The comparison of the document skips the new lines, which raw HTML can join with the text around them while every
    // block keeps its own text: no text block from before the change takes the new text, and none of the new text takes the
    // text after it. `takesTextAfter` tells that raw HTML of the new text stays open over the text after it.
    private static bool StandsApart(MarkdownPage after, MarkdownLineEdit edit, int textLine, out bool takesTextAfter)
    {
        takesTextAfter = false;
        foreach (var textBlock in MarkdownTextBlocks.Find(after))
        {
            if (textBlock.Line < edit.Line)
            {
                if (textBlock.EndLine >= textLine)
                {
                    return false;
                }
            }
            else if (textBlock.Line < edit.InsertedEnd && textBlock.EndLine >= edit.InsertedEnd)
            {
                takesTextAfter = textBlock.HoldsHtml;
                return false;
            }
        }

        return true;
    }

    // Counts the blocks of text that goes into the content of `owner`: every block at its top level must be a widget block
    // with a fence long enough to stand directly in that content, and the blocks inside them count as well. The text is
    // parsed on its own, where a slot block or a closing line has no block around it and is an invalid block. A name must
    // be new to the page, as the parser gives a name to the first block that has it and makes every other one an error.
    private static bool TryCountWidgetBlocks(
        MarkdownPage page, string text, MarkdownBlock? owner, out int blockCount, out string? refusal)
    {
        blockCount = 0;
        refusal = null;

        // Every block starts with the fence character, and most text has none.
        if (!text.Contains(MarkdownBlockFence.Character))
        {
            return true;
        }

        var parsed = MarkdownPageParser.Parse(text);
        var minimumFenceLength = GetWidgetFenceLength(owner);
        foreach (var block in parsed.Blocks)
        {
            if (block.Kind != MarkdownBlockKind.Widget)
            {
                refusal = GetBlockInTextRefusal(parsed, block);
                return false;
            }

            if (block.FenceLength < minimumFenceLength)
            {
                refusal = $"A widget in this content needs at least {minimumFenceLength} '{MarkdownBlockFence.Character}' characters.";
                return false;
            }

            blockCount += CountBlocks(block);
        }

        // Every block inside the widgets, at any depth. A style block is in no index and is no error.
        HashSet<string>? names = null;
        foreach (var block in parsed.AllBlocks)
        {
            if (block.Kind == MarkdownBlockKind.Invalid)
            {
                refusal = block.Error;
                return false;
            }

            if (block.Head?.Id is { } name)
            {
                names ??= GetNames(page);
                if (!names.Add(name))
                {
                    refusal = $"The name '{name}' is already used on this page.";
                    return false;
                }
            }
        }

        return true;
    }

    // Why a block that is no widget block cannot stand at the top level of text. A slot block and a closing line are parts of
    // a widget that the text cannot reach, and every other invalid block has its own error.
    private static string? GetBlockInTextRefusal(MarkdownPage parsed, MarkdownBlock block)
    {
        if (block.Kind == MarkdownBlockKind.Style)
        {
            return StyleBlockRefusal;
        }

        var headLine = parsed.Source.Substring(block.HeadSpan.Start, block.HeadSpan.Length);
        var isClosingLine = headLine.AsSpan().Trim().Trim(MarkdownBlockFence.Character).IsEmpty;
        return isClosingLine || (MarkdownBlockHead.TryParse(headLine, out var head, out _) && !head.IsWidget)
            ? WidgetPartRefusal
            : block.Error;
    }

    // The names of the blocks of the page, of every kind and at every depth.
    private static HashSet<string> GetNames(MarkdownPage page)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in page.AllBlocks)
        {
            if (block.Head?.Id is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The text as lines of the page: its line endings, the indentation of its content, and no empty lines around it. The
    // white space that starts its first line of text stays, such as that of an indented code block.
    private static string PrepareText(MarkdownPage page, string text, MarkdownBlock? owner, string lineEnding)
    {
        var trimmed = text.ReplaceLineEndings("\n").AsSpan().TrimEnd();
        var leadingWhiteSpace = trimmed[..(trimmed.Length - trimmed.TrimStart().Length)];
        var lines = trimmed[(leadingWhiteSpace.LastIndexOf('\n') + 1)..].ToString();
        return MarkdownBlockWriter.Indent(lines.ReplaceLineEndings(lineEnding), GetContentIndentation(page, owner), lineEnding);
    }

    // The content of a block is indented like its head line, and the page has none.
    private static string GetContentIndentation(MarkdownPage page, MarkdownBlock? owner) =>
        owner is null ? string.Empty : MarkdownBlockWriter.GetIndentation(page.Source, owner);

    // The first line of the change that is not empty and not in a new block: where the new text starts, or 0 when it has none.
    // The blocks are ordered by their head lines, and a block inside a new block is skipped with it.
    private static int FindFirstTextLine(MarkdownPage page, MarkdownLineEdit edit)
    {
        var source = page.Source;
        var line = edit.Line;
        foreach (var block in page.AllBlocks)
        {
            if (block.Line < line)
            {
                continue;
            }

            if (block.Line >= edit.InsertedEnd)
            {
                break;
            }

            if (FindTextLine(source, line, block.Line) is var textLine and > 0)
            {
                return textLine;
            }

            line = block.EndLine + 1;
        }

        return FindTextLine(source, line, edit.InsertedEnd);
    }

    // The first line from `line` up to `end`, without it, that is not empty, or 0.
    private static int FindTextLine(string source, int line, int end)
    {
        for (; line < end; line++)
        {
            if (!string.IsNullOrWhiteSpace(MarkdownBlockWriter.GetLines(source, line, line)))
            {
                return line;
            }
        }

        return 0;
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
        out string? refusal) =>
        TryInsertSlotBlock(
            page, blockSubjects, owner, slotName, configurationJson, MarkdownBlockWriter.GetSlotBlockPlacement(page, owner, slotName), out refusal);

    private static MarkdownStructureChange? TryInsertSlotBlock(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock owner,
        string slotName,
        string configurationJson,
        MarkdownBlockPlacement placement,
        out string? refusal)
    {
        if (!CanInsertOrRemoveBlocks(page, out refusal))
        {
            return null;
        }

        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(
                slotName, null, configurationJson, placement.FenceLength, placement.LineEnding, placement.HasClosingLine),
            placement.Indentation,
            placement.LineEnding);
        var source = MarkdownBlockWriter.InsertLines(page.Source, placement.Line, text, placement.LineEnding, out var edit);
        var after = MarkdownPageParser.Parse(source);
        if (MarkdownBlockAlignment.TryAlign(page, after, edit, expectedInsertedBlockCount: 1, owner) is not { } map)
        {
            refusal = $"Inserting a '{slotName}' block here would change other blocks of the page.";
            return null;
        }

        return new MarkdownStructureChange(
            new MarkdownBlockWrite(after, MapSubjects(blockSubjects, map), [], HasInsertedOrRemovedBlocks: true),
            owner,
            FindFirstBlockFrom(after, edit)?.Index ?? -1);
    }

    /// <summary>
    /// Adds new content at the end of the slot <paramref name="slot"/> of <paramref name="owner"/>, through
    /// <paramref name="insert"/>: at the end of the default content, which is before the first slot block of the owner, at
    /// the end of the content of the block of a named slot, or in a new block of the slot when the slot has none or is a list.
    /// A new block of a named slot goes before the first slot block of a slot that <paramref name="slots"/> declares after it,
    /// and otherwise where <see cref="TryAppendSlotBlock"/> appends one.
    /// </summary>
    /// <returns>
    /// Null with a refusal when the owner has no closing line or a step would change other blocks of the page, or with the
    /// refusal of <paramref name="insert"/>.
    /// </returns>
    public static MarkdownStructureChange? TryAddToSlot(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        MarkdownBlock owner,
        SlotMap slots,
        SlotDefinition slot,
        MarkdownContentInsert insert,
        out string? refusal)
    {
        if (!owner.HasClosingLine)
        {
            refusal = NoClosingLineRefusal;
            return null;
        }

        var slotBlock = slot.IsList ? null : MarkdownBlockWriter.GetSlotBlocks(owner, slot.Name).LastOrDefault();
        if (slotBlock is not null || slot.IsDefault)
        {
            var container = slotBlock ?? owner;
            return insert(page, blockSubjects, container, GetInsertLine(page, container, null), out refusal);
        }

        // An empty block of the slot first, into which the content then goes as into a block of the slot that exists. Each
        // step is aligned on its own, as in TryMove.
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, owner, slot.Name);
        if (!slot.IsList && FindFirstBlockOfLaterSlot(owner, slots, slot) is { } laterBlock)
        {
            // Written like that block, so that it ends before that block also without a closing line.
            placement = placement with
            {
                Line = laterBlock.Line,
                FenceLength = laterBlock.FenceLength,
                HasClosingLine = laterBlock.HasClosingLine,
                Indentation = MarkdownBlockWriter.GetIndentation(page.Source, laterBlock)
            };
        }

        if (TryInsertSlotBlock(page, blockSubjects, owner, slot.Name, "{}", placement, out refusal) is not { ResultBlockIndex: >= 0 } slotChange)
        {
            refusal ??= $"Inserting a '{slot.Name}' block would change other blocks of the page.";
            return null;
        }

        var slotPage = slotChange.Write.Page;
        var newSlotBlock = slotPage.AllBlocks[slotChange.ResultBlockIndex];
        var change = insert(
            slotPage, slotChange.Write.BlockSubjects, newSlotBlock, GetInsertLine(slotPage, newSlotBlock, null), out refusal);

        // The changed block is one of the page before both steps, and the new slot block is inserted even when the content
        // inserts no block.
        return change is null
            ? null
            : change with { Write = change.Write with { HasInsertedOrRemovedBlocks = true }, ChangedBlock = owner };
    }

    // The slot blocks are listed in the order of the text.
    private static MarkdownBlock? FindFirstBlockOfLaterSlot(MarkdownBlock owner, SlotMap slots, SlotDefinition slot)
    {
        var position = slots.Slots.IndexOf(slot);
        foreach (var child in owner.Children)
        {
            if (child.Kind == MarkdownBlockKind.Slot &&
                slots.Named.TryGetValue(child.Head!.Name, out var childSlot) &&
                slots.Slots.IndexOf(childSlot) > position)
            {
                return child;
            }
        }

        return null;
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

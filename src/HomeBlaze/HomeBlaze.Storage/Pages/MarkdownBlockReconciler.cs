using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What writing one block subject back did to its page.</summary>
/// <param name="Page">The page with the new text.</param>
/// <param name="BlockSubjects">The subject of every block of <paramref name="Page"/> that had one, and of every new block.</param>
/// <param name="Configurations">The configuration that was written for a subject, in the naming of a page.</param>
/// <param name="HasInsertedOrRemovedBlocks">
/// Whether a block was inserted or removed. Otherwise only the head line and the json block of one block changed.
/// </param>
internal sealed record MarkdownBlockWrite(
    MarkdownPage Page,
    IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects,
    IReadOnlyList<(IInterceptorSubject Subject, string ConfigurationJson)> Configurations,
    bool HasInsertedOrRemovedBlocks);

/// <summary>
/// Writes a block subject back into the text of its page: its configuration, and for every slot that is a
/// list of subjects of a type, a new block for a value that has none and the removal of a block whose value
/// is gone.
/// </summary>
/// <remarks>
/// The block of a value that stays is not written. A value is a block subject, so a change of it is written
/// when the value itself is saved.
/// </remarks>
internal sealed class MarkdownBlockReconciler
{
    private readonly IInterceptorSubject _owner;

    // Stays the same throughout: every change is after the head line of the owner.
    private readonly int _ownerIndex;

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly List<(IInterceptorSubject Subject, string ConfigurationJson)> _configurations;
    private MarkdownPage _page;
    private Dictionary<int, IInterceptorSubject> _subjects;
    private string? _refusal;
    private bool _hasInsertedOrRemovedBlocks;

    private MarkdownBlockReconciler(
        IInterceptorSubject owner,
        int ownerIndex,
        ConfigurableSubjectSerializer serializer,
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        string configurationJson)
    {
        _owner = owner;
        _ownerIndex = ownerIndex;
        _serializer = serializer;
        _page = page;
        _subjects = new Dictionary<int, IInterceptorSubject>(blockSubjects);
        _configurations = [(owner, configurationJson)];
    }

    /// <summary>
    /// Writes the subject of the block <paramref name="blockIndex"/>.
    /// </summary>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by block index.</param>
    /// <param name="blockIndex">The index of the block to write, which has a subject.</param>
    /// <param name="serializer">Serializes the configuration.</param>
    /// <param name="refusal">Why nothing can be written, when the result is null.</param>
    /// <returns>Null when the page cannot keep the change or the change cannot be made without touching other content.</returns>
    public static MarkdownBlockWrite? TryWrite(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        int blockIndex,
        ConfigurableSubjectSerializer serializer,
        out string? refusal)
    {
        var owner = blockSubjects[blockIndex];
        if (FindSlotWithSubjectFromTheGraph(owner) is { } slotWithSubject)
        {
            refusal = $"The slot '{slotWithSubject.Name}' of the block holds a subject that is not content of the page, so it cannot be written.";
            return null;
        }

        // Changes lines but neither the blocks nor their order, so the block table stays valid.
        var configurationJson = MarkdownBlockConfiguration.Serialize(serializer, owner, page.AllBlocks[blockIndex]);
        var reconciler = new MarkdownBlockReconciler(
            owner,
            blockIndex,
            serializer,
            MarkdownPageParser.Parse(MarkdownBlockWriter.WriteConfiguration(page.Source, page.AllBlocks[blockIndex], configurationJson)),
            blockSubjects,
            configurationJson);

        foreach (var slot in SlotMap.For(owner.GetType()).Named.Values)
        {
            if (slot is { IsList: true, IsOpen: false } && !reconciler.TryReconcile(slot))
            {
                refusal = reconciler._refusal;
                return null;
            }
        }

        refusal = null;
        return new MarkdownBlockWrite(
            reconciler._page, reconciler._subjects, reconciler._configurations, reconciler._hasInsertedOrRemovedBlocks);
    }

    // A slot that takes content holds what the page parsed from the text of its block. A subject that was set
    // there in the graph is in no text, and the reload would replace it with that content.
    private static SlotDefinition? FindSlotWithSubjectFromTheGraph(IInterceptorSubject owner)
    {
        foreach (var slot in SlotMap.For(owner.GetType()).Named.Values)
        {
            if (slot.IsOpen && !HoldsOnlyContentOfThePage(slot.GetValue(owner)))
            {
                return slot;
            }
        }

        return null;
    }

    private static bool HoldsOnlyContentOfThePage(object? slotValue)
    {
        if (slotValue is not Array values)
        {
            return slotValue is null or MarkdownFragment;
        }

        foreach (var value in values)
        {
            if (value is not (null or MarkdownFragment))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReconcile(SlotDefinition slot)
    {
        var values = GetValues(slot);
        var blocks = GetSlotBlocks(slot.Name);
        if (!AreInTheOrderOfTheirBlocks(blocks, values))
        {
            _refusal = $"The order of the '{slot.Name}' values differs from the order of their blocks. The order is changed in the text of the page.";
            return false;
        }

        if (FindNewValueOfAnotherType(slot, blocks, values) is { } valueOfAnotherType)
        {
            _refusal =
                $"A new '{slot.Name}' block cannot be written for a subject of the type {valueOfAnotherType.GetType().Name}, " +
                $"because the slot holds subjects of the type {slot.ValueType.Name}.";
            return false;
        }

        while (FindBlockWithoutValue(blocks, values) is { } block)
        {
            if (!CanInsertOrRemoveBlocks())
            {
                return false;
            }

            var source = MarkdownBlockWriter.RemoveBlock(_page.Source, block, out var edit);
            if (!TryApply(source, edit, expectedInsertedBlockCount: 0, owner: null))
            {
                _refusal = $"Removing the '{slot.Name}' block at line {block.Line} would change other blocks of the page.";
                return false;
            }

            blocks = GetSlotBlocks(slot.Name);
        }

        foreach (var value in values)
        {
            if (FindPositionOfBlock(blocks, value) < 0)
            {
                if (!TryInsert(slot, value))
                {
                    return false;
                }

                blocks = GetSlotBlocks(slot.Name);
            }
        }

        return true;
    }

    private List<IInterceptorSubject> GetValues(SlotDefinition slot) =>
        slot.GetValue(_owner) is IInterceptorSubject[] values ? [.. values.OfType<IInterceptorSubject>()] : [];

    // The text decides the order, and the reload gives the blocks of a slot to its values by position. A new
    // block goes after the last block of the slot, so a value without a block must not stand before a value
    // that has one either. A value that stands twice gets one block, so the reload would drop the second.
    private bool AreInTheOrderOfTheirBlocks(List<MarkdownBlock> blocks, List<IInterceptorSubject> values)
    {
        var lastPosition = -1;
        var hasValueWithoutBlock = false;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var position = FindPositionOfBlock(blocks, value);
            if (position < 0)
            {
                if (IndexOf(values, value) < index)
                {
                    return false;
                }

                hasValueWithoutBlock = true;
            }
            else if (hasValueWithoutBlock || position <= lastPosition)
            {
                return false;
            }
            else
            {
                lastPosition = position;
            }
        }

        return true;
    }

    // The reload creates the type of the slot for every block, and the arguments of another type are unknown to it.
    private IInterceptorSubject? FindNewValueOfAnotherType(SlotDefinition slot, List<MarkdownBlock> blocks, List<IInterceptorSubject> values)
    {
        foreach (var value in values)
        {
            if (value.GetType() != slot.ValueType && FindPositionOfBlock(blocks, value) < 0)
            {
                return value;
            }
        }

        return null;
    }

    private bool TryInsert(SlotDefinition slot, IInterceptorSubject value)
    {
        if (value is not IConfigurable)
        {
            _refusal = $"A new '{slot.Name}' block cannot be written for a subject of the type {value.GetType().Name}, which is not configurable.";
            return false;
        }

        if (!CanInsertOrRemoveBlocks())
        {
            return false;
        }

        var ownerBlock = _page.AllBlocks[_ownerIndex];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(_page, ownerBlock, slot.Name);
        var fenceLength = placement.FenceLength;

        string? content = null;
        var blockCount = 1;
        foreach (var valueSlot in SlotMap.For(value.GetType()).Named.Values)
        {
            var slotValue = valueSlot.GetValue(value);
            if (IsEmpty(slotValue))
            {
                continue;
            }

            // Anything else came from the text of a page, and the text is the only place it can be written from.
            if (!valueSlot.IsDefault || slotValue is not IConfigurable || slotValue is not IInterceptorSubject child || HasSlotContent(child))
            {
                _refusal =
                    $"The new '{slot.Name}' block cannot be written, because its slot '{valueSlot.Name}' holds " +
                    "content of a page or more than one new subject.";
                return false;
            }

            if (!CanBeNamedOnAPage(child.GetType()))
            {
                _refusal =
                    $"The new '{slot.Name}' block cannot be written, because the type {child.GetType().Name} " +
                    "of the subject in it cannot be named on a page.";
                return false;
            }

            var childJson = MarkdownBlockConfiguration.Serialize(_serializer, child, block: null);
            content = MarkdownBlockWriter.CreateBlock(
                child.GetType().FullName!, null, childJson, fenceLength + 1, placement.LineEnding, placement.HasClosingLine);
            blockCount++;
        }

        var configurationJson = MarkdownBlockConfiguration.Serialize(_serializer, value, block: null);
        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock(
                slot.Name, null, configurationJson, fenceLength, placement.LineEnding, placement.HasClosingLine, content),
            placement.Indentation,
            placement.LineEnding);

        var source = MarkdownBlockWriter.InsertLines(_page.Source, placement.Line, text, placement.LineEnding, out var edit);
        if (!TryApply(source, edit, blockCount, ownerBlock))
        {
            _refusal = $"Inserting a '{slot.Name}' block at line {placement.Line} would change other blocks of the page.";
            return false;
        }

        _subjects[MarkdownBlockWriter.GetSlotBlocks(_page.AllBlocks[_ownerIndex], slot.Name).Last().Index] = value;
        _configurations.Add((value, configurationJson));
        return true;
    }

    // The text functions count lines by line feeds, and Markdig ends a line at a lone carriage return as well,
    // so the lines of the blocks would point at other lines of the text. The head line is written by offsets.
    private bool CanInsertOrRemoveBlocks()
    {
        if (!MarkdownBlockWriter.HasLoneCarriageReturn(_page.Source))
        {
            return true;
        }

        _refusal = "Blocks cannot be inserted or removed on a page with a line ending that is a carriage return alone.";
        return false;
    }

    // Takes the new text only when every block that the change did not touch is still the same block.
    private bool TryApply(string source, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner)
    {
        var after = MarkdownPageParser.Parse(source);
        var map = MarkdownBlockAlignment.TryAlign(_page, after, edit, expectedInsertedBlockCount, owner);
        if (map is null)
        {
            return false;
        }

        var subjects = new Dictionary<int, IInterceptorSubject>(_subjects.Count);
        foreach (var (index, subject) in _subjects)
        {
            if (map[index] >= 0)
            {
                subjects[map[index]] = subject;
            }
        }

        _subjects = subjects;
        _page = after;
        _hasInsertedOrRemovedBlocks = true;
        return true;
    }

    // Only a block that has a subject is looked at, so a block that is shown as an error is never removed.
    private MarkdownBlock? FindBlockWithoutValue(List<MarkdownBlock> blocks, List<IInterceptorSubject> values)
    {
        foreach (var block in blocks)
        {
            if (_subjects.TryGetValue(block.Index, out var subject) && IndexOf(values, subject) < 0)
            {
                return block;
            }
        }

        return null;
    }

    // The position in the blocks of the slot of the block that has the value as its subject, or -1.
    private int FindPositionOfBlock(List<MarkdownBlock> slotBlocks, IInterceptorSubject value)
    {
        for (var position = 0; position < slotBlocks.Count; position++)
        {
            if (_subjects.TryGetValue(slotBlocks[position].Index, out var subject) && ReferenceEquals(subject, value))
            {
                return position;
            }
        }

        return -1;
    }

    private List<MarkdownBlock> GetSlotBlocks(string slotName) =>
        [.. MarkdownBlockWriter.GetSlotBlocks(_page.AllBlocks[_ownerIndex], slotName)];

    private static int IndexOf(List<IInterceptorSubject> values, IInterceptorSubject subject)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (ReferenceEquals(values[index], subject))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasSlotContent(IInterceptorSubject subject)
    {
        foreach (var slot in SlotMap.For(subject.GetType()).Named.Values)
        {
            if (!IsEmpty(slot.GetValue(subject)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmpty(object? slotValue) => slotValue is null or Array { Length: 0 };

    // A widget block names its type with the full name of the type. A name without a dot is read as the name
    // of a slot, and the '+' of a nested type or generic arguments are not read as a name at all, so the
    // head line is read back the way a reload reads it.
    private static bool CanBeNamedOnAPage(Type type)
    {
        if (type.FullName is not { } name)
        {
            return false;
        }

        var headLine = new MarkdownBlockHead(MarkdownBlockFence.MinimumLength, name, null, []).Format();
        return MarkdownBlockHead.TryParse(headLine, out var head, out _) && head.IsWidget && head.Name == name;
    }
}

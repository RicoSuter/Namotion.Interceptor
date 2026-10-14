using System.Collections;
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
    IReadOnlyList<KeyValuePair<IInterceptorSubject, string>> Configurations,
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
internal static class MarkdownBlockReconciler
{
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

        var state = new WriteState(page, blockSubjects, serializer);

        // Changes lines but neither the blocks nor their order, so the block table stays valid.
        var configurationJson = MarkdownBlockConfiguration.Serialize(serializer, owner);
        state.Configurations.Add(new KeyValuePair<IInterceptorSubject, string>(owner, configurationJson));
        state.Page = MarkdownPageParser.Parse(
            MarkdownBlockWriter.WriteConfiguration(page.Source, page.AllBlocks[blockIndex], configurationJson));

        foreach (var slot in SlotMap.For(owner.GetType()).Named.Values)
        {
            if (slot is { IsList: true, IsOpen: false } && !state.TryReconcile(owner, blockIndex, slot))
            {
                refusal = state.Refusal;
                return null;
            }
        }

        refusal = null;
        return new MarkdownBlockWrite(state.Page, state.Subjects, state.Configurations, state.HasInsertedOrRemovedBlocks);
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

    private sealed class WriteState(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        ConfigurableSubjectSerializer serializer)
    {
        public MarkdownPage Page { get; set; } = page;

        public Dictionary<int, IInterceptorSubject> Subjects { get; private set; } = new(blockSubjects);

        public List<KeyValuePair<IInterceptorSubject, string>> Configurations { get; } = [];

        public string? Refusal { get; private set; }

        public bool HasInsertedOrRemovedBlocks { get; private set; }

        // The index of the owner stays the same throughout: every change is after its head line.
        public bool TryReconcile(IInterceptorSubject owner, int ownerIndex, SlotDefinition slot)
        {
            var values = GetValues(owner, slot);
            if (!AreInTheOrderOfTheirBlocks(ownerIndex, slot.Name, values))
            {
                Refusal = $"The order of the '{slot.Name}' values differs from the order of their blocks. The order is changed in the text of the page.";
                return false;
            }

            if (FindNewValueOfAnotherType(ownerIndex, slot, values) is { } valueOfAnotherType)
            {
                Refusal =
                    $"A new '{slot.Name}' block cannot be written for a subject of the type {valueOfAnotherType.GetType().Name}, " +
                    $"because the slot holds subjects of the type {slot.ValueType.Name}.";
                return false;
            }

            while (FindBlockWithoutValue(ownerIndex, slot.Name, values) is { } block)
            {
                if (!CanInsertOrRemoveBlocks())
                {
                    return false;
                }

                var source = MarkdownBlockWriter.RemoveBlock(Page.Source, block, out var edit);
                if (!TryApply(source, edit, expectedInsertedBlockCount: 0, owner: null))
                {
                    Refusal = $"Removing the '{slot.Name}' block at line {block.Line} would change other blocks of the page.";
                    return false;
                }
            }

            foreach (var value in values)
            {
                if (!HasBlock(ownerIndex, slot.Name, value) && !TryInsert(ownerIndex, slot, value))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<IInterceptorSubject> GetValues(IInterceptorSubject owner, SlotDefinition slot)
        {
            var values = new List<IInterceptorSubject>();
            if (slot.GetValue(owner) is IEnumerable enumerable)
            {
                foreach (var value in enumerable)
                {
                    if (value is IInterceptorSubject subject)
                    {
                        values.Add(subject);
                    }
                }
            }

            return values;
        }

        // The text decides the order, and the reload gives the blocks of a slot to its values by position. A new
        // block goes after the last block of the slot, so a value without a block must not stand before a value
        // that has one either. A value that stands twice gets one block, so the reload would drop the second.
        private bool AreInTheOrderOfTheirBlocks(int ownerIndex, string slotName, List<IInterceptorSubject> values)
        {
            var blocks = GetSlotBlocks(ownerIndex, slotName);
            var lastPosition = -1;
            var hasValueWithoutBlock = false;
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                var position = FindPositionOfBlock(blocks, value);
                if (position < 0)
                {
                    if (StandsBefore(values, index, value))
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
        private IInterceptorSubject? FindNewValueOfAnotherType(int ownerIndex, SlotDefinition slot, List<IInterceptorSubject> values)
        {
            var blocks = GetSlotBlocks(ownerIndex, slot.Name);
            foreach (var value in values)
            {
                if (value.GetType() != slot.ValueType && FindPositionOfBlock(blocks, value) < 0)
                {
                    return value;
                }
            }

            return null;
        }

        private bool TryInsert(int ownerIndex, SlotDefinition slot, IInterceptorSubject value)
        {
            if (value is not IConfigurable)
            {
                Refusal = $"A new '{slot.Name}' block cannot be written for a subject of the type {value.GetType().Name}, which is not configurable.";
                return false;
            }

            if (!CanInsertOrRemoveBlocks())
            {
                return false;
            }

            var ownerBlock = Page.AllBlocks[ownerIndex];
            var placement = MarkdownBlockWriter.GetSlotBlockPlacement(Page, ownerBlock, slot.Name);
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
                    Refusal =
                        $"The new '{slot.Name}' block cannot be written, because its slot '{valueSlot.Name}' holds " +
                        "content of a page or more than one new subject.";
                    return false;
                }

                if (!CanBeNamedOnAPage(child.GetType()))
                {
                    Refusal =
                        $"The new '{slot.Name}' block cannot be written, because the type {child.GetType().Name} " +
                        "of the subject in it cannot be named on a page.";
                    return false;
                }

                var childJson = MarkdownBlockConfiguration.Serialize(serializer, child);
                content = MarkdownBlockWriter.CreateBlock(
                    child.GetType().FullName!, null, childJson, fenceLength + 1, placement.LineEnding, placement.HasClosingLine);
                blockCount++;
            }

            var configurationJson = MarkdownBlockConfiguration.Serialize(serializer, value);
            var text = MarkdownBlockWriter.Indent(
                MarkdownBlockWriter.CreateBlock(
                    slot.Name, null, configurationJson, fenceLength, placement.LineEnding, placement.HasClosingLine, content),
                placement.Indentation,
                placement.LineEnding);

            var source = MarkdownBlockWriter.InsertLines(Page.Source, placement.Line, text, placement.LineEnding, out var edit);
            if (!TryApply(source, edit, blockCount, ownerBlock))
            {
                Refusal = $"Inserting a '{slot.Name}' block at line {placement.Line} would change other blocks of the page.";
                return false;
            }

            var slotBlocks = GetSlotBlocks(ownerIndex, slot.Name);
            Subjects[slotBlocks[^1].Index] = value;
            Configurations.Add(new KeyValuePair<IInterceptorSubject, string>(value, configurationJson));
            return true;
        }

        // The text functions count lines by line feeds, and Markdig ends a line at a lone carriage return as well,
        // so the lines of the blocks would point at other lines of the text. The head line is written by offsets.
        private bool CanInsertOrRemoveBlocks()
        {
            if (!MarkdownBlockWriter.HasLoneCarriageReturn(Page.Source))
            {
                return true;
            }

            Refusal = "Blocks cannot be inserted or removed on a page with a line ending that is a carriage return alone.";
            return false;
        }

        // Takes the new text only when every block that the change did not touch is still the same block.
        private bool TryApply(string source, MarkdownLineEdit edit, int expectedInsertedBlockCount, MarkdownBlock? owner)
        {
            var after = MarkdownPageParser.Parse(source);
            var map = MarkdownBlockAlignment.TryAlign(Page, after, edit, expectedInsertedBlockCount, owner);
            if (map is null)
            {
                return false;
            }

            var subjects = new Dictionary<int, IInterceptorSubject>(Subjects.Count);
            foreach (var (index, subject) in Subjects)
            {
                if (map[index] >= 0)
                {
                    subjects[map[index]] = subject;
                }
            }

            Subjects = subjects;
            Page = after;
            HasInsertedOrRemovedBlocks = true;
            return true;
        }

        // Only a block that has a subject is looked at, so a block that is shown as an error is never removed.
        private MarkdownBlock? FindBlockWithoutValue(int ownerIndex, string slotName, List<IInterceptorSubject> values)
        {
            foreach (var block in GetSlotBlocks(ownerIndex, slotName))
            {
                if (Subjects.TryGetValue(block.Index, out var subject) && !Contains(values, subject))
                {
                    return block;
                }
            }

            return null;
        }

        private bool HasBlock(int ownerIndex, string slotName, IInterceptorSubject value) =>
            FindPositionOfBlock(GetSlotBlocks(ownerIndex, slotName), value) >= 0;

        // The position in the blocks of the slot of the block that has the value as its subject, or -1.
        private int FindPositionOfBlock(List<MarkdownBlock> slotBlocks, IInterceptorSubject value)
        {
            for (var position = 0; position < slotBlocks.Count; position++)
            {
                if (Subjects.TryGetValue(slotBlocks[position].Index, out var subject) && ReferenceEquals(subject, value))
                {
                    return position;
                }
            }

            return -1;
        }

        // The blocks of the slot directly inside the owner, in the order of their lines.
        private List<MarkdownBlock> GetSlotBlocks(int ownerIndex, string slotName)
        {
            var blocks = new List<MarkdownBlock>();
            foreach (var child in Page.AllBlocks[ownerIndex].Children)
            {
                if (child.Kind == MarkdownBlockKind.Slot && child.Head!.Name == slotName)
                {
                    blocks.Add(child);
                }
            }

            blocks.Sort(static (left, right) => left.Line.CompareTo(right.Line));
            return blocks;
        }

        private static bool Contains(List<IInterceptorSubject> values, IInterceptorSubject subject)
        {
            foreach (var value in values)
            {
                if (ReferenceEquals(value, subject))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StandsBefore(List<IInterceptorSubject> values, int index, IInterceptorSubject subject)
        {
            for (var earlier = 0; earlier < index; earlier++)
            {
                if (ReferenceEquals(values[earlier], subject))
                {
                    return true;
                }
            }

            return false;
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
}

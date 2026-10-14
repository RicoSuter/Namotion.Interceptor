using System.Collections;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What writing one block subject back did to its page.</summary>
/// <param name="Page">The page with the new text.</param>
/// <param name="BlockSubjects">The subject of every block of <paramref name="Page"/> that had one, and of every new block.</param>
/// <param name="Configurations">The configuration that was written for a subject, in the naming of a page.</param>
internal sealed record MarkdownBlockWrite(
    MarkdownPage Page,
    IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects,
    IReadOnlyList<KeyValuePair<IInterceptorSubject, string>> Configurations);

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
    /// <returns>Null when the text cannot be changed without losing content or changing other blocks.</returns>
    public static MarkdownBlockWrite? TryWrite(
        MarkdownPage page,
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects,
        int blockIndex,
        ConfigurableSubjectSerializer serializer,
        out string? refusal)
    {
        var owner = blockSubjects[blockIndex];
        var state = new WriteState(page, blockSubjects, serializer);

        // Changes lines but neither the blocks nor their order, so the block table stays valid.
        var configurationJson = state.Serialize(owner);
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
        return new MarkdownBlockWrite(state.Page, state.Subjects, state.Configurations);
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

        // The slots of a subject are the content of its block, so they are not part of its configuration here.
        public string Serialize(IInterceptorSubject subject)
        {
            var slots = SlotMap.For(subject.GetType());
            return slots.Named.Count == 0
                ? serializer.Serialize(subject, ConfigurationNaming.CSharpProperty)
                : serializer.Serialize(
                    subject, name => slots.FindByPropertyName(name) is not null, ConfigurationNaming.CSharpProperty);
        }

        // The index of the owner stays the same throughout: every change is after its head line.
        public bool TryReconcile(IInterceptorSubject owner, int ownerIndex, SlotDefinition slot)
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

            while (FindBlockWithoutValue(ownerIndex, slot.Name, values) is { } block)
            {
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

        private bool TryInsert(int ownerIndex, SlotDefinition slot, IInterceptorSubject value)
        {
            if (value is not IConfigurable)
            {
                Refusal = $"A new '{slot.Name}' block cannot be written for a subject of the type {value.GetType().Name}, which is not configurable.";
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

                content = MarkdownBlockWriter.CreateBlock(
                    child.GetType().FullName!, null, Serialize(child), fenceLength + 1, placement.LineEnding, placement.HasClosingLine);
                blockCount++;
            }

            var configurationJson = Serialize(value);
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

        private bool HasBlock(int ownerIndex, string slotName, IInterceptorSubject value)
        {
            foreach (var block in GetSlotBlocks(ownerIndex, slotName))
            {
                if (Subjects.TryGetValue(block.Index, out var subject) && ReferenceEquals(subject, value))
                {
                    return true;
                }
            }

            return false;
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
    }
}

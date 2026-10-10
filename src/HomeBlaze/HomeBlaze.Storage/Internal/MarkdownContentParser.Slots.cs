using System.Runtime.CompilerServices;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

// Filling the slots of a block subject from the content of its block.
public sealed partial class MarkdownContentParser
{
    // The #id of the slot block that created a slot value. A value keeps it for its lifetime, because a block
    // with a name only ever reuses the value of that name. See FindExistingSlotValue.
    private readonly ConditionalWeakTable<IInterceptorSubject, string> _slotValueNames = new();

    /// <summary>
    /// Fills the slots of <paramref name="subject"/> from the content of its block: slot blocks fill the slot
    /// they name, everything else goes to the default slot, and a slot without content is cleared.
    /// </summary>
    private async Task<BlockError?> ApplyContentAsync(
        IInterceptorSubject subject,
        Type type,
        SlotMap slots,
        BlockMarkdownSegment owner,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var ownerLine = owner.Block.Line;

        if (owner.Content.Count == 0 && slots.Named.Count == 0)
        {
            return null;
        }

        var defaultContent = new List<MarkdownSegment>();
        var slotBlocks = new List<BlockMarkdownSegment>();
        foreach (var segment in owner.Content)
        {
            if (segment is BlockMarkdownSegment { Block.Kind: MarkdownBlockKind.Slot } slotBlock)
            {
                slotBlocks.Add(slotBlock);
            }
            else
            {
                defaultContent.Add(segment);
            }
        }

        var filled = new HashSet<SlotDefinition>();
        foreach (var group in slotBlocks.GroupBy(block => block.Block.Head!.Name))
        {
            var blocks = group.ToList();
            if (!slots.Named.TryGetValue(group.Key, out var slot))
            {
                return new BlockError(blocks[0].Block.Line, slots.Named.Count == 0
                    ? $"{type.Name} has no slots, so '{group.Key}' cannot be used inside it."
                    : $"{type.Name} has no slot '{group.Key}'. Available: {string.Join(", ", slots.Named.Keys.Order())}.");
            }

            if (!slot.IsList && blocks.Count > 1)
            {
                return new BlockError(
                    blocks[1].Block.Line, $"The slot '{slot.Name}' takes one block, but there are {blocks.Count}.");
            }

            var existingValues = GetSlotValues(subject, slot);
            var unnamedPosition = 0;
            var values = new List<IInterceptorSubject>(blocks.Count);
            for (var index = 0; index < blocks.Count; index++)
            {
                var existing = FindExistingSlotValue(existingValues, blocks[index].Block.Head!.Id, ref unnamedPosition);
                var (value, error) = await CreateSlotValueAsync(slot, blocks[index], existing, state, cancellationToken);
                if (value is null)
                {
                    return error;
                }

                values.Add(value);
            }

            SetSlotValues(subject, slot, values);
            filled.Add(slot);
        }

        var hasDefaultContent = defaultContent.Exists(
            segment => segment is not HtmlMarkdownSegment html || !string.IsNullOrWhiteSpace(html.Html));

        if (hasDefaultContent)
        {
            if (slots.Default is null)
            {
                return new BlockError(ownerLine, $"{type.Name} has no default slot, so it cannot have content.");
            }

            if (filled.Contains(slots.Default))
            {
                return new BlockError(
                    ownerLine, $"The slot '{slots.Default.Name}' is filled by a block and by loose content.");
            }

            var existing = slots.Default.GetValue(subject) as IInterceptorSubject;
            var fragment = await CreateFragmentAsync(existing, defaultContent, state, cancellationToken);
            SetSlotValues(subject, slots.Default, [fragment]);
            filled.Add(slots.Default);
        }

        foreach (var slot in slots.Named.Values)
        {
            if (!filled.Contains(slot))
            {
                SetSlotValues(subject, slot, []);
            }
        }

        return null;
    }

    private async Task<(IInterceptorSubject? Value, BlockError? Error)> CreateSlotValueAsync(
        SlotDefinition slot,
        BlockMarkdownSegment segment,
        IInterceptorSubject? existing,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var block = segment.Block;
        var head = block.Head!;

        if (head.Id is not null && !state.OwnsName(block))
        {
            return (null, new BlockError(block.Line, FormatUsedTwice(head.Id)));
        }

        if (!slot.IsOpen)
        {
            var result = await CreateBlockSubjectAsync(
                slot.ValueType,
                $"The type '{slot.ValueType.FullName}' of the slot '{slot.Name}'",
                segment,
                existing,
                state,
                cancellationToken);

            if (result.Subject is not null && head.Id is not null)
            {
                _slotValueNames.AddOrUpdate(result.Subject, head.Id);
            }

            return result;
        }

        if (head.Arguments.Count > 0 || head.Id is not null || block.ConfigurationJson is not null)
        {
            return (null, new BlockError(
                block.Line, $"The slot '{slot.Name}' holds content only and takes no arguments, name or json block."));
        }

        return (await CreateFragmentAsync(existing, segment.Content, state, cancellationToken), null);
    }

    // The fragment of a slot is kept across parses, so that the subjects in it are found again by their key.
    private async Task<MarkdownFragment> CreateFragmentAsync(
        IInterceptorSubject? existing,
        IReadOnlyList<MarkdownSegment> content,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var fragment = existing as MarkdownFragment ?? new MarkdownFragment();
        fragment.Children = await BuildChildrenAsync(content, fragment.Children, state, cancellationToken);
        return fragment;
    }

    // A block with a name takes the value that the block of that name created before, wherever it is in the slot
    // now. A block without a name takes the next value that no name created, so those are reused by position
    // among themselves, like widgets without a name in the content.
    private IInterceptorSubject? FindExistingSlotValue(
        IReadOnlyList<IInterceptorSubject?> existingValues, string? name, ref int unnamedPosition)
    {
        if (name is not null)
        {
            foreach (var value in existingValues)
            {
                if (value is not null && _slotValueNames.TryGetValue(value, out var existingName) && existingName == name)
                {
                    return value;
                }
            }

            return null;
        }

        while (unnamedPosition < existingValues.Count)
        {
            var value = existingValues[unnamedPosition++];
            if (value is null || !_slotValueNames.TryGetValue(value, out _))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<IInterceptorSubject?> GetSlotValues(IInterceptorSubject subject, SlotDefinition slot)
    {
        var value = slot.GetValue(subject);
        if (slot.IsList)
        {
            return value is IInterceptorSubject?[] array ? array : [];
        }

        return [value as IInterceptorSubject];
    }

    private static void SetSlotValues(IInterceptorSubject subject, SlotDefinition slot, IReadOnlyList<IInterceptorSubject> values)
    {
        if (slot.IsList)
        {
            var array = Array.CreateInstanceFromArrayType(slot.PropertyType, values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                array.SetValue(values[index], index);
            }

            slot.SetValue(subject, array);
        }
        else
        {
            slot.SetValue(subject, values.Count > 0 ? values[0] : null);
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

// Filling the slots of a block subject from the content of its block.
public sealed partial class MarkdownContentParser
{
    // The #id of the slot block that created a slot value. A value keeps it for its lifetime, because a block
    // with a name only ever reuses the value of that name. See FindExistingSlotValue.
    private readonly ConditionalWeakTable<IInterceptorSubject, string> _slotValueNames = new();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    /// <summary>
    /// Sorts the content of a block by the slot it fills and checks everything that needs no instance: the
    /// names of the slot blocks, how many blocks a slot takes, what a slot block may carry and whether the
    /// type has a slot for the loose content.
    /// </summary>
    /// <param name="type">The type of the block subject.</param>
    /// <param name="slots">The slots of the type.</param>
    /// <param name="owner">The block.</param>
    /// <param name="ownsName">Gets whether a block is the first one in the page with its name.</param>
    /// <param name="content">The sorted content.</param>
    /// <param name="error">The error when the content does not fit.</param>
    private static bool TryArrangeContent(
        Type type,
        SlotMap slots,
        BlockMarkdownSegment owner,
        Func<MarkdownBlock, bool> ownsName,
        out BlockContent content,
        [NotNullWhen(false)] out BlockError? error)
    {
        content = default;

        var (defaultContent, slotBlocks) = MarkdownSubjectKeys.SplitContent(owner.Content);

        var slotGroups = new List<SlotBlockGroup>();
        var defaultIsFilledByBlock = false;
        foreach (var group in slotBlocks.GroupBy(block => block.Block.Head!.Name))
        {
            var blocks = group.ToList();
            if (!slots.Named.TryGetValue(group.Key, out var slot))
            {
                error = new BlockError(blocks[0].Block.Line, slots.Named.Count == 0
                    ? $"{type.Name} has no slots, so '{group.Key}' cannot be used inside it."
                    : $"{type.Name} has no slot '{group.Key}'. Available: {string.Join(", ", slots.Named.Keys.Order())}.");
                return false;
            }

            if (!slot.IsList && blocks.Count > 1)
            {
                error = new BlockError(
                    blocks[1].Block.Line, $"The slot '{slot.Name}' takes one block, but there are {blocks.Count}.");
                return false;
            }

            foreach (var block in blocks.Select(slotBlock => slotBlock.Block))
            {
                var head = block.Head!;
                if (head.Id is not null && !ownsName(block))
                {
                    error = new BlockError(block.Line, FormatUsedTwice(head.Id));
                    return false;
                }

                if (slot.IsOpen && (head.Arguments.Count > 0 || head.Id is not null || block.ConfigurationJson is not null))
                {
                    error = new BlockError(
                        block.Line, $"The slot '{slot.Name}' holds content only and takes no arguments, name or json block.");
                    return false;
                }
            }

            slotGroups.Add(new SlotBlockGroup(slot, blocks));
            defaultIsFilledByBlock |= ReferenceEquals(slot, slots.Default);
        }

        var hasDefaultContent = defaultContent.Exists(
            segment => segment is not HtmlMarkdownSegment html || !string.IsNullOrWhiteSpace(html.Html));

        var hasNoPlaceForDefaultContent = slots.Default is null || defaultIsFilledByBlock;
        if (hasDefaultContent && hasNoPlaceForDefaultContent)
        {
            // The box of an invalid block is part of the loose content. Where that has no place, the block
            // is the mistake and not content, so its own box is shown in place of the widget.
            error = FindErrorOfInvalidBlocks(owner.Block);
            if (error is not null)
            {
                return false;
            }

            if (!IsCommentOnly(defaultContent))
            {
                error = slots.Default is null
                    ? FindErrorOfBlocksBesideTheirSlotBlock(type, owner.Block) ??
                      new BlockError(owner.Block.Line, FormatNoDefaultSlot(type, owner))
                    : new BlockError(owner.Block.Line, $"The slot '{slots.Default.Name}' is filled by a block and by loose content.");
                return false;
            }

            hasDefaultContent = false;
        }

        content = new BlockContent(slotGroups, hasDefaultContent ? defaultContent : []);
        error = null;
        return true;
    }

    private static string FormatNoDefaultSlot(Type type, BlockMarkdownSegment owner)
    {
        var message = $"{type.Name} has no default slot, so it cannot have content.";

        // Without a closing line the block takes everything up to the next head line that ends it, so
        // the content is most likely meant for the block around this one. The hint is left out when a
        // slot block follows the content: a closing line before the content would leave it outside.
        return owner.Block.HasClosingLine || HasSlotBlockAfterLooseContent(owner.Content)
            ? message
            : $"{message} The content is inside this block because the block is still open there. " +
              $"A closing line '{MarkdownBlockFence.Create(owner.Block.FenceLength)}' before the content ends the block.";
    }

    private static bool HasSlotBlockAfterLooseContent(IReadOnlyList<MarkdownSegment> content)
    {
        var hasLooseContent = false;
        foreach (var segment in content)
        {
            if (segment is BlockMarkdownSegment { Block.Kind: MarkdownBlockKind.Slot })
            {
                if (hasLooseContent)
                {
                    return true;
                }
            }
            else if (segment is not HtmlMarkdownSegment html || !IsCommentOnly(html))
            {
                hasLooseContent = true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets one box for every widget block directly inside <paramref name="owner"/> whose head line ended the
    /// slot block before it, in document order. Such a block was most likely meant to be inside that slot block.
    /// </summary>
    private static BlockError? FindErrorOfBlocksBesideTheirSlotBlock(Type type, MarkdownBlock owner)
    {
        BlockError? error = null;
        for (var index = owner.Children.Count - 1; index > 0; index--)
        {
            // Only the head line of the next block can end a slot block before the block around both ends.
            var block = owner.Children[index];
            if (block.Kind != MarkdownBlockKind.Widget ||
                owner.Children[index - 1] is not { Kind: MarkdownBlockKind.Slot, HasClosingLine: false } slotBlock)
            {
                continue;
            }

            var slotName = slotBlock.Head!.Name;
            var comparison = block.FenceLength == slotBlock.FenceLength ? "as many" : "fewer";
            var comparedWith = block.FenceLength == slotBlock.FenceLength ? "as" : "than";
            var place = block.HasClosingLine ? "here and on its closing line" : "here";
            error = new BlockError(
                block.Line,
                $"This block has {comparison} '{MarkdownBlockFence.Character}' characters {comparedWith} the slot block " +
                $"'{slotName}' at line {slotBlock.Line}, so it is not inside that block but directly inside {type.Name}, " +
                $"which has no default slot. Write '{MarkdownBlockFence.Create(slotBlock.FenceLength + 1)}' {place} " +
                $"to put it inside '{slotName}'.")
            {
                Next = error
            };
        }

        return error;
    }

    /// <summary>Gets one box for every invalid block directly inside <paramref name="owner"/>, in document order.</summary>
    private static BlockError? FindErrorOfInvalidBlocks(MarkdownBlock owner)
    {
        BlockError? error = null;
        for (var index = owner.Children.Count - 1; index >= 0; index--)
        {
            var child = owner.Children[index];
            if (child.Kind == MarkdownBlockKind.Invalid)
            {
                error = new BlockError(child.Line, child.Error!) { Next = error };
            }
        }

        return error;
    }

    /// <summary>Gets whether the content is nothing but HTML comments and white space, which shows nothing.</summary>
    private static bool IsCommentOnly(List<MarkdownSegment> content) =>
        content.TrueForAll(segment => segment is HtmlMarkdownSegment html && IsCommentOnly(html));

    private static bool IsCommentOnly(HtmlMarkdownSegment html) =>
        string.IsNullOrWhiteSpace(HtmlCommentRegex().Replace(html.Html, string.Empty));

    /// <summary>
    /// Fills the slots of <paramref name="subject"/> from the content of its block: slot blocks fill the slot
    /// they name, everything else goes to the default slot, and a slot without content is cleared.
    /// </summary>
    private async Task<BlockError?> ApplyContentAsync(
        IInterceptorSubject subject,
        SlotMap slots,
        BlockContent content,
        ParseState state,
        CancellationToken cancellationToken)
    {
        if (slots.Named.Count == 0)
        {
            return null;
        }

        var filled = new HashSet<SlotDefinition>();
        foreach (var (slot, blocks) in content.SlotGroups)
        {
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

        if (content.DefaultContent.Count > 0)
        {
            var existing = slots.Default!.GetValue(subject) as IInterceptorSubject;
            var fragment = await CreateFragmentAsync(existing, content.DefaultContent, state, cancellationToken);
            SetSlotValues(subject, slots.Default, [fragment]);
            filled.Add(slots.Default);
        }

        foreach (var slot in slots.Slots)
        {
            if (!filled.Contains(slot))
            {
                SetSlotValues(subject, slot, []);
            }
        }

        return null;
    }

    // The block was checked by TryArrangeContent before the owner was created.
    private async Task<(IInterceptorSubject? Value, BlockError? Error)> CreateSlotValueAsync(
        SlotDefinition slot,
        BlockMarkdownSegment segment,
        IInterceptorSubject? existing,
        ParseState state,
        CancellationToken cancellationToken)
    {
        if (slot.IsOpen)
        {
            return (await CreateFragmentAsync(existing, segment.Content, state, cancellationToken), null);
        }

        var result = await CreateBlockSubjectAsync(
            slot.ValueType,
            $"The type '{slot.ValueType.FullName}' of the slot '{slot.Name}'",
            segment,
            existing,
            state,
            cancellationToken);

        if (result.Subject is not null && segment.Block.Head!.Id is { } id)
        {
            _slotValueNames.AddOrUpdate(result.Subject, id);
        }

        return result;
    }

    // The fragment of a slot is kept across parses, so that the subjects in it are found again by their key.
    private async Task<MarkdownFragment> CreateFragmentAsync(
        IInterceptorSubject? existing,
        IReadOnlyList<MarkdownSegment> content,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var fragment = existing as MarkdownFragment ?? new MarkdownFragment();
        var children = await BuildChildrenAsync(content, fragment.Children, state, cancellationToken);

        // A fragment that is reused is on the page, so this attaches and detaches the subjects in it. Without
        // the execution flow, see CreateOrUpdateAsync.
        using (ExecutionContext.SuppressFlow())
        {
            fragment.Children = children;
        }

        return fragment;
    }

    // A block with a name takes the value that the block of that name created before, wherever it is in the slot
    // now. A block without a name takes the next value that no name created, so those are reused by position
    // among themselves, like widgets without a name in the content.
    // The rule is described once in MarkdownSubjectKeys.GetSlotValueKey, which the write rules use to tell whether a
    // change keeps a value; both must change together.
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
        object? value;
        if (slot.IsList)
        {
            var array = Array.CreateInstanceFromArrayType(slot.PropertyType, values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                array.SetValue(values[index], index);
            }

            value = array;
        }
        else
        {
            value = values.Count > 0 ? values[0] : null;
        }

        // The subject is in the context, so this attaches the new values with what they hold and detaches
        // the values they replace. Without the execution flow, see CreateOrUpdateAsync.
        using (ExecutionContext.SuppressFlow())
        {
            slot.SetValue(subject, value);
        }
    }

    /// <summary>The slot blocks of one slot, in document order.</summary>
    private readonly record struct SlotBlockGroup(SlotDefinition Slot, IReadOnlyList<BlockMarkdownSegment> Blocks);

    /// <summary>
    /// The content of a block, sorted by the slot it fills. <see cref="DefaultContent"/> is empty when
    /// there is nothing but white space outside the slot blocks.
    /// </summary>
    private readonly record struct BlockContent(
        IReadOnlyList<SlotBlockGroup> SlotGroups, IReadOnlyList<MarkdownSegment> DefaultContent);
}

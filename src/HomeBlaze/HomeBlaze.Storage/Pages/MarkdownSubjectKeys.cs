using System.Globalization;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The keys by which a parse of a page finds the subjects of its blocks again, so that it updates them in place instead
/// of creating them anew. The content parser builds the subjects with these keys, and the write rules use the same keys
/// to tell whether a change of the page keeps a subject.
/// </summary>
internal static class MarkdownSubjectKeys
{
    /// <summary>
    /// Gets the key of a widget block in the children of a page or of a fragment: its #name, or the name of its type and
    /// its position among the segments of that content.
    /// </summary>
    public static string GetContentKey(string? id, Type type, int index) =>
        id ?? $"{type.Name}_{index.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Gets the key by which the value of a slot is reused: its #name, or its position among the blocks of the slot
    /// without a name.
    /// </summary>
    public static string GetSlotValueKey(string? id, int unnamedPosition) =>
        id is not null ? "#" + id : "@" + unnamedPosition.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Splits the content of a widget or slot block into the slot blocks directly inside it and the default content,
    /// which is everything else, in document order.
    /// </summary>
    public static (List<MarkdownSegment> DefaultContent, List<BlockMarkdownSegment> SlotBlocks) SplitContent(
        IReadOnlyList<MarkdownSegment> content)
    {
        var defaultContent = new List<MarkdownSegment>();
        var slotBlocks = new List<BlockMarkdownSegment>();
        foreach (var segment in content)
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

        return (defaultContent, slotBlocks);
    }

    /// <summary>
    /// Walks the blocks of a page the way a parse builds their subjects, and gets each block with the place of its
    /// subject: the keys from the page down to the block, each with the type and the configuration names of its block,
    /// because another type or a removed configuration value creates a subject again together with what is inside it.
    /// </summary>
    /// <param name="pageSegments">The segments of the page, from <see cref="MarkdownSegmentBuilder.Build"/>.</param>
    /// <param name="findType">Gets the type that a widget block names with its full name, or null when there is none.</param>
    /// <param name="getConfigurationNames">Gets the names of the configuration values of a block.</param>
    public static List<KeyedBlock> Walk(
        IReadOnlyList<MarkdownSegment> pageSegments,
        Func<string, Type?> findType,
        Func<MarkdownBlock, IEnumerable<string>> getConfigurationNames)
    {
        var blocks = new List<KeyedBlock>();
        WalkContent(pageSegments, string.Empty, findType, getConfigurationNames, blocks);
        return blocks;
    }

    private static void WalkContent(
        IReadOnlyList<MarkdownSegment> content,
        string path,
        Func<string, Type?> findType,
        Func<MarkdownBlock, IEnumerable<string>> getConfigurationNames,
        List<KeyedBlock> blocks)
    {
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is not BlockMarkdownSegment segment)
            {
                continue;
            }

            var block = segment.Block;
            var type = block is { Kind: MarkdownBlockKind.Widget, Head: { } head } ? findType(head.Name) : null;
            var key = type is not null
                ? GetContentKey(block.Head!.Id, type, index)
                : $"?{block.Kind}_{index.ToString(CultureInfo.InvariantCulture)}";
            var place = $"{path}/c:{key}<{type?.FullName}|{FormatNames(block, getConfigurationNames)}>";
            blocks.Add(new KeyedBlock(segment, type, place, Slot: null, ContentKey: type is null ? null : key));
            if (type is not null)
            {
                WalkOwner(segment, type, place, findType, getConfigurationNames, blocks);
            }
        }
    }

    private static void WalkOwner(
        BlockMarkdownSegment owner,
        Type type,
        string place,
        Func<string, Type?> findType,
        Func<MarkdownBlock, IEnumerable<string>> getConfigurationNames,
        List<KeyedBlock> blocks)
    {
        var slots = SlotMap.For(type);
        var (defaultContent, slotBlocks) = SplitContent(owner.Content);
        WalkContent(defaultContent, place + "/d", findType, getConfigurationNames, blocks);

        foreach (var group in slotBlocks.GroupBy(slotBlock => slotBlock.Block.Head!.Name))
        {
            slots.Named.TryGetValue(group.Key, out var slot);
            var unnamedPosition = 0;
            foreach (var slotBlock in group)
            {
                var id = slotBlock.Block.Head!.Id;
                var key = GetSlotValueKey(id, id is null ? unnamedPosition++ : 0);
                var valueType = slot is { IsOpen: false } ? slot.ValueType : null;
                var slotPlace = $"{place}/s:{group.Key}{key}<{valueType?.FullName}|{FormatNames(slotBlock.Block, getConfigurationNames)}>";
                blocks.Add(new KeyedBlock(slotBlock, valueType, slotPlace, slot, ContentKey: null));

                if (slot is null)
                {
                    continue;
                }

                if (slot.IsOpen)
                {
                    WalkContent(slotBlock.Content, slotPlace, findType, getConfigurationNames, blocks);
                }
                else
                {
                    WalkOwner(slotBlock, slot.ValueType, slotPlace, findType, getConfigurationNames, blocks);
                }
            }
        }
    }

    private static string FormatNames(MarkdownBlock block, Func<MarkdownBlock, IEnumerable<string>> getConfigurationNames) =>
        string.Join(",", getConfigurationNames(block).Order(StringComparer.Ordinal));
}

/// <summary>A block of a page with the place of its subject.</summary>
/// <param name="Segment">The segment of the block.</param>
/// <param name="Type">The type of the subject the block creates, or null for an open slot or a block without a type.</param>
/// <param name="Place">The keys from the page down to the block.</param>
/// <param name="Slot">The slot the block fills, or null for a block in the content of a page or a fragment.</param>
/// <param name="ContentKey">The key of a widget block among the children of its content, or null.</param>
internal readonly record struct KeyedBlock(
    BlockMarkdownSegment Segment, Type? Type, string Place, SlotDefinition? Slot, string? ContentKey);

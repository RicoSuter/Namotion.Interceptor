namespace HomeBlaze.Storage.Pages;

/// <summary>A slot of a block to which an add puts page content or a configured item.</summary>
/// <param name="Name">The name of the slot blocks, or null for the default slot, whose content stands directly in the block.</param>
/// <param name="Kind">What an add puts into the slot.</param>
/// <param name="ItemType">The type of an item of a slot of <see cref="MarkdownAddSlotKind.ConfiguredItem"/>, otherwise null.</param>
public sealed record MarkdownAddSlot(string? Name, MarkdownAddSlotKind Kind, Type? ItemType);

/// <summary>What an add puts into a slot of a block.</summary>
public enum MarkdownAddSlotKind
{
    /// <summary>Page content at the end of the content of the block itself, before its first slot block.</summary>
    DefaultContent,

    /// <summary>Page content at the end of the content of the one block of a named slot, whose block is added when it has none.</summary>
    NamedContent,

    /// <summary>A new block of a list slot, holding page content.</summary>
    ContentItem,

    /// <summary>A new block of a list slot of configured items, such as the rows of a list.</summary>
    ConfiguredItem
}

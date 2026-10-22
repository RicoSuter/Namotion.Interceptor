namespace HomeBlaze.Storage.Pages;

/// <summary>A slot of a block that takes page content, such as the body or the footer of a card.</summary>
/// <param name="Name">The name of the slot blocks, or null for the default slot, whose content stands directly in the block.</param>
/// <param name="Kind">How the slot takes content.</param>
/// <param name="Exists">Whether the slot holds content, which then has insert lines of its own.</param>
public sealed record MarkdownContentSlot(string? Name, MarkdownContentSlotKind Kind, bool Exists);

/// <summary>How a slot of a block takes page content.</summary>
public enum MarkdownContentSlotKind
{
    /// <summary>The content of the block itself, before its first slot block.</summary>
    Default,

    /// <summary>The content of the one block of a named slot.</summary>
    Named,

    /// <summary>A list of slot blocks, each with content of its own, to which new content adds a block.</summary>
    ListItem
}

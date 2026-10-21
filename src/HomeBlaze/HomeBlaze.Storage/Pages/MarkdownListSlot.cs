namespace HomeBlaze.Storage.Pages;

/// <summary>A slot of a block that holds a list of blocks, such as the rows of a list.</summary>
/// <param name="Name">The name of the slot blocks.</param>
/// <param name="ItemType">The type of a value of the slot, or null for a slot whose blocks hold page content.</param>
public sealed record MarkdownListSlot(string Name, Type? ItemType);

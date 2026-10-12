namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type whose blocks only lay out a Markdown page, such as a card or a tab layout: a block of the type
/// creates no subject that acts on anything and holds no secret, so an agent with the permission to edit page layout
/// may add, change, remove and move such blocks. Only single, non-secret configuration values of the type may stand
/// in such a block.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PageLayoutAttribute : Attribute
{
    /// <summary>
    /// The name of a configuration property that holds the path of an existing subject that the block shows, such as
    /// the path of a widget reference, or null when the type refers to no subject.
    /// </summary>
    public string? ReferencePathProperty { get; init; }

    /// <summary>
    /// The names of configuration properties that hold the address of a link. Such a block stays a layout block only
    /// while each of them is a relative address or uses http, https or mailto.
    /// </summary>
    public string[]? LinkProperties { get; init; }

    /// <summary>
    /// The names of configuration properties that load content from an address, such as a picture. A block that sets
    /// one of them is no layout block, like an image in the text of a page.
    /// </summary>
    public string[]? MediaProperties { get; init; }
}

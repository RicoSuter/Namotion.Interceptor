namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property that a markdown page fills with block content.
/// The property is not configuration and is never serialized.
/// </summary>
/// <remarks>
/// The property type decides what a block produces: <c>IInterceptorSubject</c> takes the content
/// as a fragment, a concrete subject type gets an instance configured from the block's arguments,
/// and an array takes one value per block.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SlotAttribute : Attribute
{
    public SlotAttribute()
    {
    }

    /// <param name="name">What one block for this slot is called on a page. Contains no dot.</param>
    public SlotAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The block name. Defaults to the property name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Whether content that is not inside a slot block goes to this slot.
    /// At most one per type, and its type must be <c>IInterceptorSubject</c>.
    /// </summary>
    public bool IsDefault { get; set; }
}

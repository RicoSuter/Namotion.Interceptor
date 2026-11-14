namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type whose blocks only lay out a Markdown page, such as a card or a tab layout: a block of the type
/// creates no subject that acts on anything and holds no secret, so an agent with the permission to edit page layout
/// may add, change, remove and move such blocks. Only single, non-secret configuration values of the type may stand
/// in such a block. A path of a <see cref="SubjectPathAttribute"/> with a subject type must lead to an existing subject,
/// a <see cref="LinkAddressAttribute"/> must be safe, and a <see cref="MediaAddressAttribute"/> must not be set.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PageLayoutAttribute : Attribute;

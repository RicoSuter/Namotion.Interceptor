namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a configuration property of a <see cref="PageLayoutAttribute"/> type that loads content from an address, such
/// as a picture. A block that sets it is no layout block, like an image in the text of a page.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PageLayoutMediaAttribute : Attribute;

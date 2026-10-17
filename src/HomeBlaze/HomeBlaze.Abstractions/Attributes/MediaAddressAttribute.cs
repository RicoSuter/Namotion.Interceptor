namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a string property that holds the address of content that is loaded and shown, such as a picture. Agents may
/// not set it, like an image in the text of a page.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MediaAddressAttribute : Attribute;

namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a configuration property of a <see cref="PageLayoutAttribute"/> type that holds the address of a link. A
/// block stays a layout block only while the address is relative or uses http, https or mailto.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PageLayoutLinkAttribute : Attribute;

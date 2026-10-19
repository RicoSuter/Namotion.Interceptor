namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a string property that holds the address of a link, relative to the page or absolute. Content that agents
/// may change accepts only a relative address or one with http, https or mailto there.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LinkAddressAttribute : Attribute;

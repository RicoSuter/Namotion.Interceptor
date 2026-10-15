namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a configuration property of a <see cref="PageLayoutAttribute"/> type that holds the path of an existing
/// subject the block shows, such as the path of a widget reference. An agent's layout edit that sets the path must
/// lead to an existing subject.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PageLayoutReferenceAttribute : Attribute;

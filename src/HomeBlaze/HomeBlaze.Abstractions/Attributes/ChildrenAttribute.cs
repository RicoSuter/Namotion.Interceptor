namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a dictionary property as the default child container for path resolution.
/// Child keys become directly accessible in paths without the property name.
/// Existing properties take precedence over child keys when resolving paths.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChildrenAttribute : Attribute
{
}

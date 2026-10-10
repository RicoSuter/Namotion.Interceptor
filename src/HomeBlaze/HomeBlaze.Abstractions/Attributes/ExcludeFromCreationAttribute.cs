namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is never offered in the create dialog, because it only exists as part of another subject.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExcludeFromCreationAttribute : Attribute;

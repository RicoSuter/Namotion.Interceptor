namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is never offered in the create dialog, because it only exists as part of another subject.
/// It applies to the exact type and is not inherited, so a derived type is offered unless it is marked itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExcludeFromCreationAttribute : Attribute;

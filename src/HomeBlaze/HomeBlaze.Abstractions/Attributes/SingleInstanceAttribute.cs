namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is offered in the create dialog only while no instance of it exists in the subject graph.
/// It applies to the exact type and is not inherited, and an instance of a derived type does not count as one of it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingleInstanceAttribute : Attribute;

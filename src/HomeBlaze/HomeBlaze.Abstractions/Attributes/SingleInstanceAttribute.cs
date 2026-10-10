namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type that is offered in the create dialog only while no instance of it exists in the subject graph.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingleInstanceAttribute : Attribute;

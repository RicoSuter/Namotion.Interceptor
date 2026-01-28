namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a method parameter to be resolved from dependency injection.
/// Parameters without this attribute are treated as user-provided input.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute
{
}

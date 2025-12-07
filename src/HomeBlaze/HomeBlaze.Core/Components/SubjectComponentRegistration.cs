using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Core.Components;

/// <summary>
/// Represents a registered component for a subject type.
/// </summary>
/// <param name="ComponentType">The Blazor component type.</param>
/// <param name="SubjectType">The subject type this component is for.</param>
/// <param name="Type">The type of component (Page, Edit, Widget).</param>
/// <param name="Name">Optional name to distinguish multiple components of the same type.</param>
public record SubjectComponentRegistration(
    Type ComponentType,
    Type SubjectType,
    SubjectComponentType Type,
    string? Name
);

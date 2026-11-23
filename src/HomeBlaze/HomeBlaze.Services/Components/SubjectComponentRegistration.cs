using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;

namespace HomeBlaze.Services.Components;

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
)
{
    /// <summary>Gets whether the component implements <see cref="ISubjectDiscoveryComponent"/> and so takes a discovered device.</summary>
    public bool TakesDiscoveryResults => typeof(ISubjectDiscoveryComponent).IsAssignableFrom(ComponentType);
}

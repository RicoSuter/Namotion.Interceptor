namespace HomeBlaze.Components.Abstractions.Attributes;

/// <summary>
/// Registers a Blazor component as a view for a subject type.
/// Used to provide custom UI components for specific subject types.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class SubjectComponentAttribute : Attribute
{
    /// <summary>
    /// Gets the type of component (Page, Edit, Widget).
    /// </summary>
    public SubjectComponentType ComponentType { get; }

    /// <summary>
    /// Gets the subject type this component is for.
    /// </summary>
    public Type SubjectType { get; }

    /// <summary>
    /// Gets or sets an optional name for the component.
    /// Used to distinguish between multiple components of the same type for the same subject.
    /// </summary>
    public string? Name { get; set; }

    /// <param name="componentType">The type of component (Page, Edit, Widget)</param>
    /// <param name="subjectType">The subject type this component is for</param>
    public SubjectComponentAttribute(SubjectComponentType componentType, Type subjectType)
    {
        ComponentType = componentType;
        SubjectType = subjectType ?? throw new ArgumentNullException(nameof(subjectType));
    }
}

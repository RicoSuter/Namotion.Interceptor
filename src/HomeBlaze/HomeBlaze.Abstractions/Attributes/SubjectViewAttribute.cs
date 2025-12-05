namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Registers a Blazor component as a view for a subject type.
/// Used to provide custom UI components for specific subject types in the browser.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class SubjectViewAttribute : Attribute
{
    /// <summary>
    /// The type of view (e.g., "edit", "widget", "setup").
    /// </summary>
    public string ViewType { get; }

    /// <summary>
    /// The subject type this view is for.
    /// </summary>
    public Type SubjectType { get; }

    /// <param name="viewType">The type of view (e.g., "edit", "widget", "setup")</param>
    /// <param name="subjectType">The subject type this view is for</param>
    public SubjectViewAttribute(string viewType, Type subjectType)
    {
        if (string.IsNullOrEmpty(viewType))
            throw new ArgumentException("ViewType cannot be null or empty", nameof(viewType));

        ViewType = viewType.ToLowerInvariant();
        SubjectType = subjectType ?? throw new ArgumentNullException(nameof(subjectType));
    }
}

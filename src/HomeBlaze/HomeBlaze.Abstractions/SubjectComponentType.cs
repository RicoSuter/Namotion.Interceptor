namespace HomeBlaze.Abstractions;

/// <summary>
/// Defines the type of UI component for a subject.
/// </summary>
public enum SubjectComponentType
{
    /// <summary>
    /// A full page view for the subject.
    /// </summary>
    Page,

    /// <summary>
    /// An edit/configuration view for the subject.
    /// </summary>
    Edit,

    /// <summary>
    /// A compact widget view for the subject.
    /// </summary>
    Widget
}

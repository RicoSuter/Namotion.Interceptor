using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History;

/// <summary>
/// Builds the path the history of a property is recorded and queried under.
/// </summary>
public static class HistoryPaths
{
    /// <summary>
    /// Gets the path the history of the property is recorded under, or null when its subject has no
    /// canonical path.
    /// </summary>
    public static string? GetHistoryPath(this RegisteredSubjectProperty property, ISubjectPathResolver resolver)
    {
        var subjectPath = resolver.GetPath(property.Subject, PathStyle.Canonical);
        return subjectPath is null ? null : Join(subjectPath, property.Name);
    }

    /// <summary>
    /// Joins a canonical subject path and a property name: "/Temperature" for the root subject "/",
    /// "/Child/Temperature" for "/Child".
    /// </summary>
    public static string Join(string subjectPath, string propertyName) =>
        subjectPath == "/" ? "/" + propertyName : subjectPath + "/" + propertyName;
}

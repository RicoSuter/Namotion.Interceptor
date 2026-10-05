using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.Paths;

public static class PathExtensions
{
    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="path">The path to the property from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The result specifying whether the path could be found and the value has been applied.</returns>
    public static bool UpdatePropertyValueFromPath(this IInterceptorSubject subject, string path, DateTimeOffset timestamp, object? value, PathProviderBase pathProvider, object? source)
    {
        return subject
            .UpdatePropertyValueFromPath(path, timestamp, (_, _) => value, pathProvider, source);
    }

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="path">The path to the property from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="getPropertyValue">The function to retrieve the property value to set.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The result specifying whether the path could be found and the value has been applied.</returns>
    public static bool UpdatePropertyValueFromPath(this IInterceptorSubject subject,
        string path, DateTimeOffset timestamp,
        Func<RegisteredSubjectProperty, string, object?> getPropertyValue,
        PathProviderBase pathProvider, object? source)
    {
        return subject
            .VisitPropertiesFromPathsWithTimestamp([path], timestamp,
                (property, innerPath, _) => SetPropertyValue(property, timestamp, getPropertyValue(property, innerPath), source), pathProvider)
            .Count == 1;
    }

    /// <summary>
    /// Sets the value of multiple properties and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject"></param>
    /// <param name="paths">The paths to the properties from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="getPropertyValue">The function to retrieve the property value.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns></returns>
    public static IEnumerable<string> UpdatePropertyValuesFromPaths(this IInterceptorSubject subject, IEnumerable<string> paths, DateTimeOffset timestamp, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, PathProviderBase pathProvider, object? source)
    {
        return subject
            .VisitPropertiesFromPathsWithTimestamp(paths, timestamp, (property, path, _) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), pathProvider);
    }

    /// <summary>
    /// Sets the value of multiple properties and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathsAndValues">The source paths and values to apply.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The list of visited paths.</returns>
    public static IEnumerable<string> UpdatePropertyValuesFromPaths(this IInterceptorSubject subject, IReadOnlyDictionary<string, object?> pathsAndValues, DateTimeOffset timestamp, PathProviderBase pathProvider, object? source)
    {
        return subject
            .VisitPropertiesFromPathsWithTimestamp(pathsAndValues.Keys, timestamp, (property, path, _) => SetPropertyValue(property, timestamp, pathsAndValues[path], source), pathProvider);
    }

    private static IReadOnlyCollection<string> VisitPropertiesFromPathsWithTimestamp(this IInterceptorSubject subject,
        IEnumerable<string> paths, DateTimeOffset timestamp, Action<RegisteredSubjectProperty, string, object?> visitProperty,
        PathProviderBase pathProvider, ISubjectFactory? subjectFactory = null)
    {
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            return subject.VisitPropertiesFromPaths(paths, visitProperty, pathProvider, subjectFactory);
        }
    }

    private static void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset timestamp, object? value, object? source)
    {
        if (source is not null)
        {
            property.SetValueFromSource(source, timestamp, null, value);
        }
        else
        {
            property.SetValue(value);
        }
    }

    /// <summary>
    /// Visits all path leaf properties using source paths and returns the paths which have been found and visited.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="paths">The source paths to apply values.</param>
    /// <param name="visitProperty">The callback to visit a property.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing referenced subjects within the path (optional).</param>
    /// <returns>The list of visited paths.</returns>
    public static IReadOnlyCollection<string> VisitPropertiesFromPaths(this IInterceptorSubject subject,
        IEnumerable<string> paths, Action<RegisteredSubjectProperty, string, object?> visitProperty,
        PathProviderBase pathProvider, ISubjectFactory? subjectFactory = null)
    {
        var visitedPaths = new List<string>();
        foreach (var path in paths)
        {
            var (property, index) = subject.TryGetPropertyFromPath(path, pathProvider, subjectFactory);
            if (property is not null)
            {
                visitProperty(property, path, index);
                visitedPaths.Add(path);
            }
        }

        return visitedPaths.AsReadOnly();
    }

    /// <summary>
    /// Tries to get a property from the source path. Only properties the provider includes are resolved.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="path">The source path of the property to look up.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="subjectFactory">
    /// The subject factory to create missing referenced subjects within the path (optional). Missing collection and
    /// dictionary items are not created; such a path is not found.
    /// </param>
    /// <returns>The found property and the key of its last segment, or nulls when not found.</returns>
    public static (RegisteredSubjectProperty? property, object? index) TryGetPropertyFromPath(
        this IInterceptorSubject subject, string path, PathProviderBase pathProvider, ISubjectFactory? subjectFactory = null)
    {
        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return (null, null);
        }

        var result = pathProvider.TryGetPropertyFromPath(registeredSubject, path, includedPropertiesOnly: true,
            subjectFactory is null ? null : property => CreateSubject(property, subjectFactory));

        return result is { } found ? (found.Property, found.Index) : (null, null);
    }

    /// <summary>
    /// Tries to get multiple properties from the source paths. Only properties the provider includes are resolved.
    /// </summary>
    /// <param name="rootSubject">The root subject.</param>
    /// <param name="paths">The source paths of the properties to look up.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing referenced subjects within the path (optional).</param>
    /// <returns>Each path with its property and last-segment key, or null property when not found; a path without segments yields nothing.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty? property, object? index)> GetPropertiesFromPaths(
        this IInterceptorSubject rootSubject,
        IEnumerable<string> paths,
        PathProviderBase pathProvider,
        ISubjectFactory? subjectFactory = null)
    {
        foreach (var path in paths)
        {
            if (path.AsSpan().IndexOfAnyExcept(pathProvider.PathSeparator) < 0)
            {
                continue;
            }

            var (property, index) = rootSubject.TryGetPropertyFromPath(path, pathProvider, subjectFactory);
            yield return (path, property, index);
        }
    }

    private static IInterceptorSubject CreateSubject(RegisteredSubjectProperty property, ISubjectFactory subjectFactory)
    {
        var subject = subjectFactory.CreateSubject(property);
        property.SetValue(subject);
        return subject;
    }
}

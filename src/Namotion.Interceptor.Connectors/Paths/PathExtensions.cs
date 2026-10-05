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
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The list of visited paths.</returns>
    public static IReadOnlyCollection<string> VisitPropertiesFromPaths(this IInterceptorSubject subject,
        IEnumerable<string> paths, Action<RegisteredSubjectProperty, string, object?> visitProperty,
        PathProviderBase pathProvider, ISubjectFactory? subjectFactory = null)
    {
        var visitedPaths = new List<string>();
        foreach (var (path, property, index) in subject.GetPropertiesFromPaths(paths, pathProvider, subjectFactory, useCache: false))
        {
            if (property is not null)
            {
                visitProperty(property, path, index);
                visitedPaths.Add(path);
            }
        }

        return visitedPaths.AsReadOnly();
    }

    /// <summary>
    /// Tries to get a property from the source path.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="path">The source path of the property to look up.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The found subject property or null if it is not found and factory was null.</returns>
    public static (RegisteredSubjectProperty? property, object? index) TryGetPropertyFromPath(
        this IInterceptorSubject subject, string path, PathProviderBase pathProvider, ISubjectFactory? subjectFactory = null)
    {
        var (_, property, index) = subject
            .GetPropertiesFromPaths([path], pathProvider, subjectFactory, useCache: false)
            .FirstOrDefault();

        return (property, index);
    }

    /// <summary>
    /// Tries to get multiple properties from the source paths.
    /// </summary>
    /// <param name="rootSubject">The root subject.</param>
    /// <param name="paths">The source path of the property to look up.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <param name="useCache">Defines whether to use a method-scoped property path cache, only useful when passing multiple similar paths.</param>
    /// <returns>The found subject properties with the typed key of their last segment; a malformed or unresolved path yields a null property, an empty path yields nothing.</returns>
    /// <exception cref="InvalidOperationException">The provider's separator and index characters are not distinct.</exception>
    public static IEnumerable<(string path, RegisteredSubjectProperty? property, object? index)> GetPropertiesFromPaths(
        this IInterceptorSubject rootSubject,
        IEnumerable<string> paths,
        PathProviderBase pathProvider,
        ISubjectFactory? subjectFactory = null,
        bool useCache = true)
    {
        // Keyed by the path text up to the end of a segment: the same text always parses the same way, so the key
        // needs no escaping. Looked up by span so a hit allocates no key.
        var pathValueCache = useCache
            ? new Dictionary<string, (RegisteredSubjectProperty property, object? key, IInterceptorSubject? subject)>()
                .GetAlternateLookup<ReadOnlySpan<char>>()
            : default;

        var characters = pathProvider.GetCharacters();
        foreach (var path in paths)
        {
            var position = 0;
            if (string.IsNullOrEmpty(path) || !PathSyntax.SkipToSegment(characters, path, ref position))
            {
                continue;
            }

            // Segments are read while walking, so a malformed tail is only found on the way. Without a factory the
            // walk has no side effects; with one it may create subjects, so the whole path is checked first.
            if (subjectFactory is not null && !PathSyntax.IsWellFormed(characters, path))
            {
                yield return (path, null, null);
                continue;
            }

            var currentSubject = rootSubject;
            while (true)
            {
                if (!PathSyntax.TryReadSegment(characters, path, ref position, out var segment, out _))
                {
                    yield return (path, null, null);
                    break;
                }

                var isLastSegment = !PathSyntax.SkipToSegment(characters, path, ref position);

                RegisteredSubjectProperty? property;
                object? key;
                IInterceptorSubject? nextSubject;
                if (useCache &&
                    pathValueCache.TryGetValue(path.AsSpan(0, segment.End), out var entry) &&
                    (isLastSegment || entry.subject is not null))
                {
                    property = entry.property;
                    key = entry.key;
                    nextSubject = isLastSegment ? null : entry.subject;
                }
                else
                {
                    var registeredSubject = currentSubject.TryGetRegisteredSubject();
                    if (registeredSubject is null ||
                        !pathProvider.TryResolveSegment(registeredSubject, path, characters, segment, out property, out key, out var child) ||
                        !pathProvider.IsPropertyIncluded(property))
                    {
                        yield return (path, null, null);
                        break;
                    }

                    nextSubject = null;
                    if (!isLastSegment)
                    {
                        nextSubject = key is not null
                            ? GetItemOrThrowWhenCreating(child, subjectFactory)
                            : TryGetReferencedSubjectOrCreate(property, subjectFactory);

                        if (nextSubject is null)
                        {
                            yield return (path, null, null);
                            break;
                        }
                    }

                    if (useCache)
                    {
                        pathValueCache[path.AsSpan(0, segment.End)] = (property, key, nextSubject);
                    }
                }

                if (isLastSegment)
                {
                    yield return (path, property, key);
                    break;
                }

                currentSubject = nextSubject!;
            }
        }
    }

    private static IInterceptorSubject? GetItemOrThrowWhenCreating(IInterceptorSubject? child, ISubjectFactory? subjectFactory)
    {
        if (child is null && subjectFactory is not null)
        {
            // TODO: Implement collection or dictionary creation from paths (need to know all paths).
            throw new InvalidOperationException("Missing collection items cannot be created.");
        }

        return child;
    }

    private static IInterceptorSubject? TryGetReferencedSubjectOrCreate(RegisteredSubjectProperty registeredProperty, ISubjectFactory? subjectFactory)
    {
        // TODO: Use registeredProperty.IsSubjectReference here instead
        if (!registeredProperty.Type.IsAssignableTo(typeof(IInterceptorSubject)))
        {
            return null;
        }

        // TODO(perf): Use nextSubject = registeredProperty.GetValue() as IInterceptorSubject;
        var nextSubject = registeredProperty.Children.SingleOrDefault().Subject;
        if (nextSubject is null && subjectFactory is not null)
        {
            nextSubject = subjectFactory.CreateSubject(registeredProperty);
            registeredProperty.SetValue(nextSubject);
        }

        return nextSubject;
    }
}

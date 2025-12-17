using System.Text;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Change;

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
                (property, path, _) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), pathProvider)
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
            return VisitPropertiesFromPaths(subject, paths, visitProperty, pathProvider, subjectFactory);
        }
    }

    private static void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset timestamp, object? value, object? source)
    {
        if (source is not null)
        {
            property.SetValueFromSource(source, timestamp, value);
        }
        else
        {
            property.SetValue(value);
        }
    }

    /// <summary>
    /// Gets the complete source path of the given property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The path.</returns>
    public static string? TryGetPath(this RegisteredSubjectProperty property, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        var propertiesInPath = property
            .GetPropertiesInPath(rootSubject)
            .ToArray();

        if (propertiesInPath.Length > 0 &&
            pathProvider.IsPropertyIncluded(propertiesInPath.Last().property))
        {
            return pathProvider.GetPath(propertiesInPath);
        }

        return null;
    }

    /// <summary>
    /// Gets all complete source paths of the given properties.
    /// </summary>
    /// <param name="properties">The properties.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetPaths(
        this IEnumerable<RegisteredSubjectProperty> properties, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var property in properties)
        {
            var path = property.TryGetPath(pathProvider, rootSubject);
            if (path is not null)
            {
                yield return (path, property);
            }
        }
    }

    /// <summary>
    /// Gets all complete source paths of the given property changes.
    /// </summary>
    /// <param name="changes">The changes.</param>
    /// <param name="pathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<(string path, SubjectPropertyChange change)> GetPaths(
        this IEnumerable<SubjectPropertyChange> changes, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var change in changes)
        {
            var registeredProperty = change.Property.TryGetRegisteredProperty();
            if (registeredProperty is not null)
            {
                var path = TryGetPath(registeredProperty, pathProvider, rootSubject);
                if (path is not null)
                {
                    yield return (path, change);
                }
            }
        }
    }

    /// <summary>
    /// Get bread crumb properties in path (i.e. reverse list of parents).
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="rootSubject">The root subject.</param>
    /// <returns>The list of properties between the root subject and the property.</returns>
    public static IEnumerable<(RegisteredSubjectProperty property, object? index)> GetPropertiesInPath(this RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        return GetPropertiesInPathReverse(property, rootSubject)
            .Reverse();
    }

    private static IEnumerable<(RegisteredSubjectProperty property, object? index)> GetPropertiesInPathReverse(RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        SubjectPropertyParent? pathWithProperty = new SubjectPropertyParent { Property = property };
        do
        {
            property = pathWithProperty.Value.Property;
            yield return (property ?? throw new InvalidOperationException("Property is null."), pathWithProperty.Value.Index);
            pathWithProperty = property?.Parent.Subject != rootSubject
                ? property?.Parent.Parents.FirstOrDefault()
                : null;
        } while (pathWithProperty?.Property is not null);
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
        foreach (var (path, property, index) in
            GetPropertiesFromPaths(subject, paths, pathProvider, subjectFactory, useCache: false))
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
    /// <returns>The found subject properties.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty? property, object? index)> GetPropertiesFromPaths(
        this IInterceptorSubject rootSubject,
        IEnumerable<string> paths,
        PathProviderBase pathProvider,
        ISubjectFactory? subjectFactory = null,
        bool useCache = true)
    {
        var pathValueCache = useCache
            ? new Dictionary<string, (RegisteredSubjectProperty property, IInterceptorSubject? subject)>()
            : null;

        foreach (var path in paths)
        {
            var segments = pathProvider.ParsePath(path);
            if (segments.Count == 0)
            {
                continue;
            }

            var currentSubject = rootSubject;
            var currentPath = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                var (segment, index) = segments[i];
                var isLastSegment = i == segments.Count - 1;

                string? currentPathString = null;
                if (pathValueCache is not null)
                {
                    if (currentPath.Length > 0) currentPath.Append(":/:.:");
                    currentPath.Append(segment);
                    if (index is not null)
                    {
                        currentPath.Append('[').Append(index).Append(']');
                    }
                    currentPathString = currentPath.ToString();
                }

                RegisteredSubjectProperty? property;
                IInterceptorSubject? nextSubject;
                if (pathValueCache?.TryGetValue(currentPathString!, out var entry) == true)
                {
                    // load property from cache
                    property = entry.property;
                    nextSubject = !isLastSegment
                        ? entry.subject ?? TryGetPropertySubjectOrCreate(entry.property, index, subjectFactory)
                        : null;
                }
                else
                {
                    // look up property in subject & add to cache
                    var registeredSubject = currentSubject.TryGetRegisteredSubject();
                    if (registeredSubject is null)
                    {
                        yield return (path, null, null);
                        break;
                    }

                    // Attribute lookup is not supported - resolve as regular property
                    property = pathProvider.TryGetPropertyFromSegment(registeredSubject, segment);

                    if (property is null ||
                        pathProvider.IsPropertyIncluded(property) == false)
                    {
                        yield return (path, null, null);
                        break;
                    }

                    if (!isLastSegment)
                    {
                        nextSubject = TryGetPropertySubjectOrCreate(property, index, subjectFactory);
                        if (nextSubject is null)
                        {
                            yield return (path, null, null);
                            break;
                        }
                    }
                    else
                    {
                        nextSubject = null;
                    }

                    pathValueCache?.Add(currentPathString!, (property, nextSubject));
                }

                if (isLastSegment)
                {
                    yield return (path, property, index);
                }
                else
                {
                    currentSubject = nextSubject!;
                }
            }
        }
    }

    private static IInterceptorSubject? TryGetPropertySubjectOrCreate(RegisteredSubjectProperty registeredProperty, object? index, ISubjectFactory? subjectFactory)
    {
        IInterceptorSubject? nextSubject;
        if (index is not null)
        {
            // TODO: Move to common value handle extension methods
            // nextSubject = index is not int
            //     ? (registeredProperty.GetValue() as IDictionary)?[index] as IInterceptorSubject
            //     : (registeredProperty.GetValue() as IList)?[(int)index] as IInterceptorSubject;

            nextSubject = registeredProperty
                .Children
                .SingleOrDefault(c => Equals(c.Index, index))
                .Subject;

            if (nextSubject is null && subjectFactory is not null)
            {
                // create missing item collection or item dictionary

                throw new InvalidOperationException("Missing collection items cannot be created.");
                // TODO: Implement collection or dictionary creation from paths (need to know all paths).

                // currentSubject = subjectFactory.CreateSubject(registeredProperty, null);
                // var collection  = subjectFactory.CreateSubjectCollection(registeredProperty, currentSubject);
                // registeredProperty.SetValue(collection);
            }
        }
        else if (registeredProperty.Type.IsAssignableTo(typeof(IInterceptorSubject)))
        {
            // TODO(perf): Use nextSubject = registeredProperty.GetValue() as IInterceptorSubject;
            nextSubject = registeredProperty.Children.SingleOrDefault().Subject;

            if (nextSubject is null && subjectFactory is not null)
            {
                nextSubject = subjectFactory.CreateSubject(registeredProperty);
                registeredProperty.SetValue(nextSubject);
            }
        }
        else
        {
            nextSubject = null;
        }

        return nextSubject;
    }
}

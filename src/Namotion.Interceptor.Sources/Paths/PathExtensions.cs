using System.Text;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Paths;

public static class PathExtensions
{
    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="sourcePath">The path to the property from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The result specifying whether the path could be found and the value has been applied.</returns>
    public static bool UpdatePropertyValueFromSourcePath(this IInterceptorSubject subject, string sourcePath, DateTimeOffset timestamp, object? value, ISourcePathProvider sourcePathProvider, ISubjectSource? source)
    {
        return subject
            .UpdatePropertyValueFromSourcePath(sourcePath, timestamp, (_, _) => value, sourcePathProvider, source);
    }

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="sourcePath">The path to the property from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="getPropertyValue">The function to retrieve the property value to set.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The result specifying whether the path could be found and the value has been applied.</returns>
    public static bool UpdatePropertyValueFromSourcePath(this IInterceptorSubject subject,
        string sourcePath, DateTimeOffset timestamp,
        Func<RegisteredSubjectProperty, string, object?> getPropertyValue,
        ISourcePathProvider sourcePathProvider, ISubjectSource? source)
    {
        return subject
            .VisitPropertiesFromSourcePathsWithTimestamp([sourcePath], timestamp, 
                (property, path, _) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), sourcePathProvider)
            .Count == 1;
    }

    /// <summary>
    /// Sets the value of multiple properties and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject"></param>
    /// <param name="sourcePaths">The paths to the properties from the source's perspective.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="getPropertyValue">The function to retrieve the property value.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns></returns>
    public static IEnumerable<string> UpdatePropertyValuesFromSourcePaths(this IInterceptorSubject subject, IEnumerable<string> sourcePaths, DateTimeOffset timestamp, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider, ISubjectSource? source)
    {
        return subject
            .VisitPropertiesFromSourcePathsWithTimestamp(sourcePaths, timestamp, (property, path, _) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), sourcePathProvider);
    }

    /// <summary>
    /// Sets the value of multiple properties and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathsAndValues">The source paths and values to apply.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The list of visited paths.</returns>
    public static IEnumerable<string> UpdatePropertyValuesFromSourcePaths(this IInterceptorSubject subject, IReadOnlyDictionary<string, object?> pathsAndValues, DateTimeOffset timestamp, ISourcePathProvider sourcePathProvider, ISubjectSource? source)
    {
        return subject
            .VisitPropertiesFromSourcePathsWithTimestamp(pathsAndValues.Keys, timestamp, (property, path, _) => SetPropertyValue(property, timestamp, pathsAndValues[path], source), sourcePathProvider);
    }

    private static IReadOnlyCollection<string> VisitPropertiesFromSourcePathsWithTimestamp(this IInterceptorSubject subject,
        IEnumerable<string> sourcePaths, DateTimeOffset timestamp, Action<RegisteredSubjectProperty, string, object?> visitProperty,
        ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        return SubjectMutationContext.ApplyChangesWithTimestamp(timestamp,
            () => VisitPropertiesFromSourcePaths(subject, sourcePaths, visitProperty, sourcePathProvider, subjectFactory));
    }

    private static void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset timestamp, object? value, ISubjectSource? source)
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
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The path.</returns>
    public static string? TryGetSourcePath(this RegisteredSubjectProperty property, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        var propertiesInPath = property
            .GetPropertiesInPath(rootSubject)
            .ToArray();

        if (propertiesInPath.Length > 0 &&
            sourcePathProvider.IsPropertyIncluded(propertiesInPath.Last().property))
        {
            return sourcePathProvider.GetPropertyFullPath(propertiesInPath);
        }

        return null;
    }

    /// <summary>
    /// Gets all complete source paths of the given properties.
    /// </summary>
    /// <param name="properties">The properties.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetSourcePaths(
        this IEnumerable<RegisteredSubjectProperty> properties, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var property in properties)
        {
            var path = property.TryGetSourcePath(sourcePathProvider, rootSubject);
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
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<(string path, SubjectPropertyChange change)> GetSourcePaths(
        this IEnumerable<SubjectPropertyChange> changes, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var change in changes)
        {
            var path = TryGetSourcePath(change.Property.GetRegisteredProperty(), sourcePathProvider, rootSubject);
            if (path is not null)
            {
                yield return (path, change);
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
            pathWithProperty = property?.Parent?.Subject != rootSubject
                ? property?.Parent?.Parents?.FirstOrDefault()
                : null;
        } while (pathWithProperty?.Property is not null);
    }

    /// <summary>
    /// Visits all path leaf properties using source paths and returns the paths which have been found and visited.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="sourcePaths">The source paths to apply values.</param>
    /// <param name="visitProperty">The callback to visit a property.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The list of visited paths.</returns>
    public static IReadOnlyCollection<string> VisitPropertiesFromSourcePaths(this IInterceptorSubject subject,
        IEnumerable<string> sourcePaths, Action<RegisteredSubjectProperty, string, object?> visitProperty,
        ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        var visitedPaths = new List<string>();
        foreach (var (path, property, index) in GetPropertiesFromSourcePaths(subject, sourcePaths, sourcePathProvider, subjectFactory))
        {
            visitProperty(property, path, index);
            visitedPaths.Add(path);
        }

        return visitedPaths.AsReadOnly();
    }

    /// <summary>
    /// Tries to get a property from the source path.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="sourcePath">The source path of the property to look up.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The found subject property or null if it is not found and factory was null.</returns>
    public static (RegisteredSubjectProperty? property, object? index) TryGetPropertyFromSourcePath(
        this IInterceptorSubject subject, string sourcePath, ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        var currentSubject = subject;
        var segments = sourcePathProvider
            .ParsePathSegments(sourcePath)
            .ToArray();

        RegisteredSubjectProperty? parentProperty = null;
        for (var i = 0; i < segments.Length; i++)
        {
            var (segment, index) = segments[i];
            var isLastSegment = i == segments.Length - 1;

            var registeredSubject = currentSubject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                return (null, null);
            }

            var registeredProperty = parentProperty?.IsAttribute == true
                ? sourcePathProvider.TryGetAttributeFromSegment(parentProperty, segment)
                : sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);

            if (registeredProperty is null ||
                sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
            {
                return (null, null);
            }

            if (isLastSegment)
            {
                return (registeredProperty, index);
            }

            currentSubject = TryGetPropertySubjectOrCreate(registeredProperty, index, subjectFactory);
            if (currentSubject is null)
            {
                return (null, null);
            }

            parentProperty = registeredProperty;
        }

        return (null, null);
    }

    /// <summary>
    /// Tries to get multiple properties from the source paths.
    /// </summary>
    /// <param name="rootSubject">The root subject.</param>
    /// <param name="sourcePaths">The source path of the property to look up.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The found subject properties.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty propertym, object? index)> GetPropertiesFromSourcePaths(
        this IInterceptorSubject rootSubject,
        IEnumerable<string> sourcePaths,
        ISourcePathProvider sourcePathProvider,
        ISubjectFactory? subjectFactory = null)
    {
        var cache = new Dictionary<string, (RegisteredSubjectProperty property, IInterceptorSubject? subject)>();
        foreach (var sourcePath in sourcePaths)
        {
            var segments = sourcePathProvider.ParsePathSegments(sourcePath).ToArray();
            if (segments.Length == 0)
            {
                continue;
            }

            var currentSubject = rootSubject;
            RegisteredSubjectProperty? parentProperty = null;

            var sb = new StringBuilder();
            for (var i = 0; i < segments.Length; i++)
            {
                var (segment, index) = segments[i];
                var isLast = i == segments.Length - 1;

                if (sb.Length > 0) sb.Append('/');
                sb.Append(segment);
                if (index is not null)
                {
                    sb.Append('[').Append(index).Append(']');
                }
                var prefix = sb.ToString();

                RegisteredSubjectProperty? property;
                IInterceptorSubject? nextSubject;
                if (cache.TryGetValue(prefix, out var entry))
                {
                    property = entry.property;
                    if (!isLast)
                    {
                        var subject = entry.subject ?? TryGetPropertySubjectOrCreate(entry.property, index, subjectFactory);
                        if (!ReferenceEquals(subject, entry.subject))
                        {
                            cache[prefix] = (entry.property, subject);
                        }
                        nextSubject = subject;
                        if (nextSubject is null)
                        {
                            break;
                        }
                    }
                    else
                    {
                        nextSubject = null;
                    }
                }
                else
                {
                    if (parentProperty?.IsAttribute == true)
                    {
                        property = sourcePathProvider.TryGetAttributeFromSegment(parentProperty, segment);
                    }
                    else
                    {
                        var registeredSubject = currentSubject.TryGetRegisteredSubject();
                        if (registeredSubject is null)
                        {
                            break;
                        }

                        property = sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);
                    }

                    if (property is null || sourcePathProvider.IsPropertyIncluded(property) == false)
                    {
                        break;
                    }

                    if (!isLast)
                    {
                        nextSubject = TryGetPropertySubjectOrCreate(property, index, subjectFactory);
                        cache[prefix] = (property, nextSubject);
                        if (nextSubject is null)
                        {
                            break;
                        }
                    }
                    else
                    {
                        nextSubject = null;
                        cache[prefix] = (property, null);
                    }
                }

                if (isLast)
                {
                    yield return (sourcePath, property, index);
                }
                else
                {
                    currentSubject = nextSubject!;
                }

                parentProperty = property;
            }
        }
    }

    private static IInterceptorSubject? TryGetPropertySubjectOrCreate(RegisteredSubjectProperty registeredProperty, object? index, ISubjectFactory? subjectFactory)
    {
        IInterceptorSubject? nextSubject;
        if (index is not null)
        {
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
            nextSubject = registeredProperty.Children.SingleOrDefault().Subject;

            if (nextSubject is null && subjectFactory is not null)
            {
                nextSubject = subjectFactory.CreateSubject(registeredProperty, null);
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

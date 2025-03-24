using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Paths;

public static class PathExtensions
{
    /// <summary>
    /// Gets a list of all properties of the subject and child subjects with their source paths.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetAllRegisteredPropertiesWithSourcePaths(this RegisteredSubject subject, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .GetAllProperties()
            .GetSourcePaths(sourcePathProvider, subject.Subject)
            .ToArray() ?? [];
    }

    /// <summary>
    /// Sets the value of the property and marks the assignment as applied by the specified source (optional).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="sourcePath">The path to the property from the source's perspective.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="source">The optional source to mark the write as coming from this source to avoid updates.</param>
    /// <returns>The result specifying whether the path could be found and the value has been applied.</returns>
    public static bool UpdatePropertyValueFromSourcePath(this IInterceptorSubject subject, string sourcePath, object? value, DateTimeOffset timestamp, ISourcePathProvider sourcePathProvider, ISubjectSource? source)
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
            .VisitPropertiesFromSourcePathsWithTimestamp([sourcePath], timestamp, (property, path) => SetPropertyValue(property, getPropertyValue(property, path), timestamp, source), sourcePathProvider)
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
            .VisitPropertiesFromSourcePathsWithTimestamp(sourcePaths, timestamp, (property, path) => SetPropertyValue(property, getPropertyValue(property, path), timestamp, source), sourcePathProvider);
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
            .VisitPropertiesFromSourcePathsWithTimestamp(pathsAndValues.Keys, timestamp, (property, path) => SetPropertyValue(property, pathsAndValues[path], timestamp, source), sourcePathProvider);
    }

    private static IReadOnlyCollection<string> VisitPropertiesFromSourcePathsWithTimestamp(this IInterceptorSubject subject,
        IEnumerable<string> sourcePaths, DateTimeOffset timestamp, Action<RegisteredSubjectProperty, string> visitProperty,
        ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        PropertyChangedObservable.SetCurrentTimestamp(timestamp);
        try
        {
            return VisitPropertiesFromSourcePaths(subject, sourcePaths, visitProperty, sourcePathProvider, subjectFactory);
        }
        finally
        {
            PropertyChangedObservable.ResetCurrentTimestamp();
        }
    }

    private static void SetPropertyValue(RegisteredSubjectProperty property, object? value, DateTimeOffset timestamp, ISubjectSource? source)
    {
        if (source is not null)
        {
            property.SetValueFromSource(source, value);
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
    /// <param name="subjectFactory">The subject factory.</param>
    /// <returns>The list of visited paths.</returns>
    public static IReadOnlyCollection<string> VisitPropertiesFromSourcePaths(this IInterceptorSubject subject, 
        IEnumerable<string> sourcePaths, Action<RegisteredSubjectProperty, string> visitProperty, 
        ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        // TODO(perf): Optimize for multiple paths (group by)

        var foundPaths = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            var currentSubject = subject;
            var segments = sourcePathProvider
                .ParsePathSegments(sourcePath)
                .ToArray();

            RegisteredSubjectProperty? parentProperty = null!;
            for (var i = 0; i < segments.Length; i++)
            {
                var (segment, index) = segments[i];
                var isLastSegment = i == segments.Length - 1;

                var registeredSubject = currentSubject.TryGetRegisteredSubject()
                    ?? throw new InvalidOperationException("Registered subject not found.");

                var registeredProperty = parentProperty?.IsAttribute == true
                    ? sourcePathProvider.TryGetAttributeFromSegment(parentProperty, segment)
                    : sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);

                if (registeredProperty is null ||
                    sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
                {
                    break;
                }

                if (!isLastSegment)
                {
                    if (index is not null)
                    {
                        // handle array or dictionary item update
                        currentSubject = registeredProperty
                            .Children
                            .SingleOrDefault(c => Equals(c.Index, index))
                            .Subject;

                        if (currentSubject is null && subjectFactory is not null)
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
                        // handle item update
                        currentSubject = registeredProperty.Children.SingleOrDefault().Subject;

                        if (currentSubject is null && subjectFactory is not null)
                        {
                            // create missing item
                            currentSubject = subjectFactory.CreateSubject(registeredProperty, null);
                            registeredProperty.SetValue(currentSubject);
                        }
                    }
                }
                else if (index is null) // paths to collection items are ignored, e.g. "foo/bar[0]"
                {
                    visitProperty(registeredProperty, sourcePath);
                    foundPaths.Add(sourcePath);
                    break;
                }

                if (currentSubject is null)
                {
                    break;
                }

                parentProperty = registeredProperty;
            }
        }

        return foundPaths.AsReadOnly();
    }
}
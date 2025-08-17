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
            .VisitPropertiesFromSourcePathsWithTimestamp([sourcePath], timestamp, (property, path) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), sourcePathProvider)
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
            .VisitPropertiesFromSourcePathsWithTimestamp(sourcePaths, timestamp, (property, path) => SetPropertyValue(property, timestamp, getPropertyValue(property, path), source), sourcePathProvider);
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
            .VisitPropertiesFromSourcePathsWithTimestamp(pathsAndValues.Keys, timestamp, (property, path) => SetPropertyValue(property, timestamp, pathsAndValues[path], source), sourcePathProvider);
    }

    private static IReadOnlyCollection<string> VisitPropertiesFromSourcePathsWithTimestamp(this IInterceptorSubject subject,
        IEnumerable<string> sourcePaths, DateTimeOffset timestamp, Action<RegisteredSubjectProperty, string> visitProperty,
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
        IEnumerable<string> sourcePaths, Action<RegisteredSubjectProperty, string> visitProperty,
        ISourcePathProvider sourcePathProvider, ISubjectFactory? subjectFactory = null)
    {
        var visitedPaths = new List<string>();
        foreach (var (path, property) in GetPropertiesFromSourcePaths(subject, sourcePaths, sourcePathProvider, subjectFactory))
        {
            visitProperty(property, path);
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
    public static RegisteredSubjectProperty? TryGetPropertyFromSourcePath(
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
                return null;
            }

            var registeredProperty = parentProperty?.IsAttribute == true
                ? sourcePathProvider.TryGetAttributeFromSegment(parentProperty, segment)
                : sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);

            if (registeredProperty is null ||
                sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
            {
                return null;
            }

            if (isLastSegment)
            {
                if (index is null)
                {
                    return registeredProperty;
                }
            }
            else
            {
                currentSubject = TryGetNextSubject(registeredProperty, index, subjectFactory);
            }

            if (currentSubject is null)
            {
                return null;
            }

            parentProperty = registeredProperty;
        }

        return null;
    }

    /// <summary>
    /// Tries to get multiple properties from the source paths.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="sourcePaths">The source path of the property to look up.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects within the path (optional).</param>
    /// <returns>The found subject properties.</returns>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetPropertiesFromSourcePaths(
        this IInterceptorSubject subject,
        IEnumerable<string> sourcePaths,
        ISourcePathProvider sourcePathProvider,
        ISubjectFactory? subjectFactory = null)
    {
        var rootNode = new PathNode();
        foreach (var sourcePath in sourcePaths.Distinct())
        {
            var currentNode = rootNode;
            var segments = sourcePathProvider.ParsePathSegments(sourcePath);
            foreach (var segment in segments)
            {
                if (!currentNode.Children.TryGetValue(segment, out var childNode))
                {
                    childNode = new PathNode();
                    currentNode.Children[segment] = childNode;
                }

                currentNode = childNode;
            }

            currentNode.FullPath = sourcePath;
        }

        var stack = new Stack<(IInterceptorSubject Subject, PathNode Node, RegisteredSubjectProperty? ParentProperty)>();
        stack.Push((subject, rootNode, null));

        while (stack.Count > 0)
        {
            var (currentSubject, pathNode, parentProperty) = stack.Pop();
            var registeredSubject = currentSubject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            foreach (var childPathNode in pathNode.Children)
            {
                var (segment, index) = childPathNode.Key;
                var nextPathNode = childPathNode.Value;

                var registeredProperty = parentProperty?.IsAttribute == true
                    ? sourcePathProvider.TryGetAttributeFromSegment(parentProperty, segment)
                    : sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);

                if (registeredProperty is null || sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
                {
                    continue;
                }

                if (nextPathNode.FullPath is not null && index is null)
                {
                    yield return (nextPathNode.FullPath, registeredProperty);
                }

                if (nextPathNode.Children.Count > 0)
                {
                    var childSubject = TryGetNextSubject(registeredProperty, index, subjectFactory);
                    if (childSubject is not null)
                    {
                        stack.Push((childSubject, nextPathNode, registeredProperty));
                    }
                }
            }
        }
    }

    private static IInterceptorSubject? TryGetNextSubject(RegisteredSubjectProperty registeredProperty, object? index, ISubjectFactory? subjectFactory)
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

    private class PathNode
    {
        public string? FullPath { get; set; }

        public Dictionary<(string Segment, object? Index), PathNode> Children { get; } = new();
    }
}

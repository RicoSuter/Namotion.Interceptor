using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Paths;

public static class PathExtensions
{
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetAllRegisteredPropertiesWithSourcePaths(this RegisteredSubject subject, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .GetAllProperties()
            .GetSourcePaths(sourcePathProvider, subject.Subject)
            .ToArray() ?? [];
    }

    public static bool ApplyValueFromSourcePath(this IInterceptorSubject subject, string sourcePath, object? value, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .ApplyValueFromSourcePath(sourcePath, (_, _) => value, sourcePathProvider);
    }

    public static bool ApplyValueFromSourcePath(this IInterceptorSubject subject, string sourcePath, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitPropertiesFromSourcePaths([sourcePath], (property, path) => property.SetValue(getPropertyValue(property, path)), sourcePathProvider)
            .Count == 1;
    }

    public static IEnumerable<string> ApplyValuesFromSourcePaths(this IInterceptorSubject subject, IEnumerable<string> sourcePaths, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitPropertiesFromSourcePaths(sourcePaths, (property, path) => property.SetValue(getPropertyValue(property, path)), sourcePathProvider);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathsAndValues">The source paths and values to apply.</param>
    /// <param name="sourcePathProvider">The source path provider.</param>
    /// <returns>The list of visited paths.</returns>
    public static IEnumerable<string> ApplyValuesFromSourcePaths(this IInterceptorSubject subject, IReadOnlyDictionary<string, object?> pathsAndValues, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitPropertiesFromSourcePaths(pathsAndValues.Keys, (property, path) => property.SetValue(pathsAndValues[path]), sourcePathProvider);
    }

    public static string? TryGetSourcePath(this RegisteredSubjectProperty property, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        var propertiesInPath = property
            .GetPropertiesInPath(rootSubject)
            .ToArray();

        if (propertiesInPath.Length > 0 && 
            propertiesInPath.All(p => sourcePathProvider.IsPropertyIncluded(p.property)))
        {
            return propertiesInPath.Aggregate("", 
                (pathPrefix, path) => 
                    sourcePathProvider.GetPropertyFullPath(pathPrefix, path.property, path.index));
        }

        return null;
    }

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
        (RegisteredSubjectProperty property, object? index)? pathWithProperty = (property, null);
        do
        {
            property = pathWithProperty.Value.property;
            yield return (property ?? throw new InvalidOperationException("Property is null."), pathWithProperty.Value.index);
            pathWithProperty = property?.Parent?.Subject != rootSubject 
                ? property?.Parent?.Parents?.FirstOrDefault()
                : null;
        } while (pathWithProperty?.property is not null);
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

                var registry = currentSubject.Context.GetService<ISubjectRegistry>();
                var registeredSubject = registry.KnownSubjects[currentSubject];

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
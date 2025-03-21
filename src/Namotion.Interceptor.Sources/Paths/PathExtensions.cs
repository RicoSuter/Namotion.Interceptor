using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Paths;

public static class PathExtensions
{
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetRegisteredPropertiesWithSourcePaths(this IInterceptorSubject subject, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .TryGetRegisteredSubject()?
            .GetAllProperties()
            .GetSourcePaths(sourcePathProvider, subject)
            .ToArray() ?? [];
    }
    
    public static bool ApplyValueFromSource(this IInterceptorSubject subject, string sourcePath, object? value, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .ApplyValueFromSource(sourcePath, (_, _) => value, sourcePathProvider);
    }
    
    public static bool ApplyValueFromSource(this IInterceptorSubject subject, string sourcePath, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitValuesFromSource([sourcePath], (property, path) => property.SetValue(getPropertyValue(property, path)), sourcePathProvider)
            .Length == 1;
    }
    
    public static IEnumerable<string> ApplyValuesFromSource(this IInterceptorSubject subject, IEnumerable<string> sourcePaths, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitValuesFromSource(sourcePaths, (property, path) => property.SetValue(getPropertyValue(property, path)), sourcePathProvider);
    }
    
    public static IEnumerable<string> ApplyValuesFromSource(this IInterceptorSubject subject, IReadOnlyDictionary<string, object?> pathsAndValues, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .VisitValuesFromSource(pathsAndValues.Keys, (property, path) => property.SetValue(pathsAndValues[path]), sourcePathProvider);
    }
    
    /// <summary>
    /// Visits all path leaf properties using source paths and returns the paths which have been found and visited.
    /// </summary>
    /// <param name="subject"></param>
    /// <param name="sourcePaths"></param>
    /// <param name="visitPropertyValue"></param>
    /// <param name="sourcePathProvider"></param>
    /// <returns></returns>
    public static string[] VisitValuesFromSource(this IInterceptorSubject subject, IEnumerable<string> sourcePaths, 
        Action<RegisteredSubjectProperty, string> visitPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        // TODO(perf): Optimize for multiple paths
        // TODO: Add support to create missing items/collections
        
        var foundPaths = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            var currentSubject = subject;
            var segments = sourcePathProvider
                .ParsePathSegments(sourcePath)
                .ToArray();

            for (var i = 0; i < segments.Length; i++)
            {
                var (segment, index) = segments[i];
                var isLastSegment = i == segments.Length - 1;

                var registry = currentSubject.Context.GetService<ISubjectRegistry>();
                var registeredSubject = registry.KnownSubjects[currentSubject];

                var registeredProperty = sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);
                if (registeredProperty is null ||
                    sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
                {
                    break;
                }

                if (!isLastSegment && index is not null)
                {
                    // handle array or dictionary item update
                    currentSubject = registeredProperty
                        .Children
                        .SingleOrDefault(c => Equals(c.Index, index))
                        .Subject;

                }
                else if (!isLastSegment && 
                         registeredProperty.Type.IsAssignableTo(typeof(IInterceptorSubject)))
                {
                    currentSubject = registeredProperty.Children.SingleOrDefault().Subject;
                }
                else if (isLastSegment)
                {
                    visitPropertyValue(registeredProperty, sourcePath);
                    foundPaths.Add(sourcePath);
                    break;
                }

                if (currentSubject is null)
                {
                    break;
                }
            }
        }

        return foundPaths.ToArray();
    }

    public static string? TryGetSourcePath(this RegisteredSubjectProperty property, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        var propertiesInPath = property
            .GetPropertiesInPath(rootSubject)
            .ToArray();

        if (propertiesInPath.Length > 0 && sourcePathProvider.IsPropertyIncluded(propertiesInPath.Last()))
        {
            return propertiesInPath.Aggregate("", sourcePathProvider.GetPropertyFullPath);
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

    public static IEnumerable<(string path, SubjectPropertyUpdate change)> GetSourcePaths(
        this IEnumerable<SubjectPropertyUpdate> changes, ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
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

    public static IEnumerable<RegisteredSubjectProperty> GetPropertiesInPath(this RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        return GetPropertiesInPathReverse(property, rootSubject)
            .Reverse();
    }

    private static IEnumerable<RegisteredSubjectProperty> GetPropertiesInPathReverse(RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        var registeredProperty = property;
        do
        {
            yield return registeredProperty ?? throw new InvalidOperationException("Property is null.");
            registeredProperty = registeredProperty?.Parent?.Parents?.FirstOrDefault();
        } while (registeredProperty is not null && registeredProperty?.Parent.Subject != rootSubject);
    }
}
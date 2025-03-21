using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Paths;

public static class PathExtensions
{
    public static bool ApplyValueFromSource(this IInterceptorSubject subject, string sourcePath, object? value, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .ApplyValueFromSource(sourcePath, (_, _) => value, sourcePathProvider);
    }
    
    public static bool ApplyValueFromSource(this IInterceptorSubject subject, string sourcePath, Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .ApplyValuesFromSource([sourcePath], getPropertyValue, sourcePathProvider)
            .Length == 1;
    }
    
    public static IEnumerable<string> ApplyValuesFromSource(this IInterceptorSubject subject, IReadOnlyDictionary<string, object?> pathsAndValues, ISourcePathProvider sourcePathProvider)
    {
        return subject
            .ApplyValuesFromSource(pathsAndValues.Keys, (_, path) => pathsAndValues[path], sourcePathProvider);
    }
    
    /// <summary>
    /// Applies values from the source to the subject and returns the paths which have been found and updated.
    /// </summary>
    /// <param name="subject"></param>
    /// <param name="sourcePaths"></param>
    /// <param name="getPropertyValue"></param>
    /// <param name="sourcePathProvider"></param>
    /// <returns></returns>
    public static string[] ApplyValuesFromSource(this IInterceptorSubject subject, IEnumerable<string> sourcePaths, 
        Func<RegisteredSubjectProperty, string, object?> getPropertyValue, ISourcePathProvider sourcePathProvider)
    {
        // TODO(perf): Optimize for multiple paths
        // TODO: Add support to create missing items/collections
        
        var foundPaths = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            RegisteredSubjectProperty? previousProperty = null;
            var currentSubject = subject;
            var segments = sourcePathProvider
                .ParsePathSegments(sourcePath)
                .ToArray();

            for (var i = 0; i < segments.Length; i++)
            {
                var (segment, index, isAttribute) = segments[i];
                var isLastSegment = i == segments.Length - 1;

                var registry = currentSubject.Context.GetService<ISubjectRegistry>();
                var registeredSubject = registry.KnownSubjects[currentSubject];

                var registeredProperty = isAttribute ? 
                    previousProperty?.Property.TryGetRegisteredAttribute(segment) : 
                    sourcePathProvider.TryGetPropertyFromSegment(registeredSubject, segment);
            
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
                    registeredProperty.SetValue(getPropertyValue(registeredProperty, sourcePath));
                    foundPaths.Add(sourcePath);
                    break;
                }

                if (currentSubject is null)
                {
                    break;
                }

                previousProperty = registeredProperty;
            }
        }

        return foundPaths.ToArray();
    }

    public static IEnumerable<(string path, PropertyChangedContext change)> ConvertToSourcePaths(this IEnumerable<PropertyChangedContext> changes,
        ISourcePathProvider sourcePathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var change in changes)
        {
            yield return (change
                .Property
                .GetRegisteredProperty()
                .EnumerateProperties(rootSubject)
                .Aggregate("", sourcePathProvider.GetPropertyFullPath), change);
        }
    }

    public static IEnumerable<RegisteredSubjectProperty> EnumerateProperties(this RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        return EnumeratePropertiesReverse(property, rootSubject)
            .Reverse();
    }

    private static IEnumerable<RegisteredSubjectProperty> EnumeratePropertiesReverse(RegisteredSubjectProperty property, IInterceptorSubject? rootSubject)
    {
        var registeredProperty = property;
        do
        {
            yield return registeredProperty ?? throw new InvalidOperationException("Property is null.");
            registeredProperty = registeredProperty?.Parent?.Parents?.FirstOrDefault();
        } while (registeredProperty is null && registeredProperty?.Parent.Subject != rootSubject);
    }
}
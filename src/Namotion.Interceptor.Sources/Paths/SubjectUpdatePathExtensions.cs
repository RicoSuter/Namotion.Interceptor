using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public static class SubjectUpdatePathExtensions
{
    /// <summary>
    /// Creates a partial subject update with the given path and given value.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="path">The path.</param>
    /// <param name="value">The value.</param>
    /// <param name="sourcePathProvider">The source path provider to resolve paths.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateSubjectUpdateFromPath(
        this IInterceptorSubject subject,
        string path,
        object? value,
        ISourcePathProvider sourcePathProvider)
    {
        return subject.CreateSubjectUpdateFromPaths([path], sourcePathProvider, 
            (_, _) => value);
    }

    /// <summary>
    /// Creates a partial subject update with the given path and the value retrieve function.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="path">The path.</param>
    /// <param name="sourcePathProvider">The source path provider to resolve paths.</param>
    /// <param name="getPropertyValue">The function to resolve a property value, called per path.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateSubjectUpdateFromPath(
        this IInterceptorSubject subject,
        string path,
        ISourcePathProvider sourcePathProvider,
        Func<RegisteredSubjectProperty, string, object?> getPropertyValue)
    {
        return subject.CreateSubjectUpdateFromPaths([path], sourcePathProvider, getPropertyValue);
    }

    /// <summary>
    /// Creates a partial subject update with the given paths and values.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="pathsWithValues">The dictionary with paths and values.</param>
    /// <param name="sourcePathProvider">The source path provider to resolve paths.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateSubjectUpdateFromPaths(
        this IInterceptorSubject subject,
        IReadOnlyDictionary<string, object?> pathsWithValues,
        ISourcePathProvider sourcePathProvider)
    {
        return subject.CreateSubjectUpdateFromPaths(pathsWithValues.Keys, sourcePathProvider, 
            (_, path) => pathsWithValues[path]);
    }

    /// <summary>
    /// Creates a partial subject update with the given path and the value retrieve function.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="paths">The paths.</param>
    /// <param name="sourcePathProvider">The source path provider to resolve paths.</param>
    /// <param name="getPropertyValue">The function to resolve a property value, called per path.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateSubjectUpdateFromPaths(
        this IInterceptorSubject subject,
        IEnumerable<string> paths,
        ISourcePathProvider sourcePathProvider,
        Func<RegisteredSubjectProperty, string, object?> getPropertyValue)
    {
        var update = new SubjectUpdate();
        RegisteredSubjectProperty? previousProperty = null;

        foreach (var path in paths)
        {
            var currentSubject = subject;
            var currentUpdate = update;
            foreach (var (segment, isAttribute) in sourcePathProvider.ParsePathSegments(path))
            {
                var segmentParts = segment.Split('[', ']');
                object? index = segmentParts.Length >= 2 ? (int.TryParse(segmentParts[1], out var intIndex) ? intIndex : segmentParts[1]) : null;
                var propertyName = segmentParts[0];

                var registry = currentSubject.Context.GetService<ISubjectRegistry>();
                var registeredSubject = registry.KnownSubjects[currentSubject];

                var registeredProperty = isAttribute
                    ? previousProperty?.Property.GetRegisteredAttribute(segment) ?? throw new InvalidOperationException("Attribute segment must have a property path segment before.")
                    : registeredSubject.Properties[propertyName];

                if (sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
                {
                    break;
                }

                if (index is not null) // handle array or dictionary item update
                {
                    var item = registeredProperty.Children.Single(c => Equals(c.Index, index));

                    var childUpdates = registeredProperty
                        .Children
                        .Select(c => new SubjectPropertyCollectionUpdate
                        {
                            Index = c.Index ?? throw new InvalidOperationException($"Index of collection property '{registeredProperty.Property.Name}' must not be null."),
                            Item = new SubjectUpdate()
                        })
                        .ToList();

                    currentUpdate.Properties[propertyName] = new SubjectPropertyUpdate
                    {
                        Action = SubjectPropertyUpdateAction.UpdateCollection,
                        Collection = childUpdates
                    };

                    currentUpdate = childUpdates.Single(u => Equals(u.Index, index)).Item!;
                    currentSubject = item.Subject;
                }
                else if (registeredProperty.Type.IsAssignableTo(typeof(IInterceptorSubject))) // handle item update
                {
                    var item = registeredProperty.Children.Single();
                    var childUpdate = new SubjectUpdate();
                    currentUpdate.Properties[propertyName] = new SubjectPropertyUpdate
                    {
                        Action = SubjectPropertyUpdateAction.UpdateItem,
                        Item = childUpdate
                    };

                    currentUpdate = childUpdate;
                    currentSubject = item.Subject;
                }
                else // handle value update
                {
                    currentUpdate.Properties[propertyName] = new SubjectPropertyUpdate
                    {
                        Action = SubjectPropertyUpdateAction.UpdateValue,
                        Value = getPropertyValue(registeredProperty, path),
                    };
                    break;
                }

                previousProperty = registeredProperty;
            }
        }
        
        return update;
    }

    public static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(
        this SubjectUpdate subjectUpdate,
        IInterceptorSubject subject,
        ISourcePathProvider sourcePathProvider,
        string pathPrefix = "")
    {
        foreach (var property in subjectUpdate.Properties)
        {
            foreach (var (path, value, registeredProperty) in property.Value
                .EnumeratePaths(subject, property.Key, sourcePathProvider, pathPrefix))
            {
                yield return (path, value, registeredProperty);
            }
        }
    }

    private static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(
        this SubjectPropertyUpdate propertyUpdate,
        IInterceptorSubject subject, string propertyName,
        ISourcePathProvider sourcePathProvider,
        string pathPrefix = "")
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName) ?? throw new KeyNotFoundException(propertyName);
        if (sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
        {
            yield break;
        }

        var propertyPath = registeredProperty.IsAttribute ? sourcePathProvider.GetPropertyAttributePath(pathPrefix, registeredProperty) : sourcePathProvider.GetPropertyPath(pathPrefix, registeredProperty);

        if (propertyUpdate.Attributes is not null)
        {
            foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
            {
                var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                foreach (var (path, value, property) in attributeUpdate
                             .EnumeratePaths(subject, registeredAttribute.Property.Name, sourcePathProvider, propertyPath))
                {
                    yield return (path, value, property);
                }
            }
        }

        switch (propertyUpdate.Action)
        {
            case SubjectPropertyUpdateAction.UpdateValue: // handle value
                yield return (propertyPath, propertyUpdate.Value, registeredProperty);
                break;

            case SubjectPropertyUpdateAction.UpdateItem: // handle item
                if (registeredProperty.GetValue() is IInterceptorSubject currentItem)
                {
                    foreach (var (path, value, property) in propertyUpdate.Item!
                                 .EnumeratePaths(currentItem, sourcePathProvider, propertyPath))
                    {
                        yield return (path, value, property);
                    }
                }
                else
                {
                    // TODO: Handle missing item
                }

                break;

            case SubjectPropertyUpdateAction.UpdateCollection: // handle array or dictionary
                var collection = registeredProperty.GetValue()!;
                foreach (var item in propertyUpdate.Collection!)
                {
                    if (item.Item is null)
                    {
                        continue;
                    }

                    var currentCollectionItem = item.Index is int ? ((ICollection<IInterceptorSubject>)collection).ElementAt(Convert.ToInt32(item.Index)) : ((IDictionary)collection)[item.Index] as IInterceptorSubject;

                    if (currentCollectionItem is not null)
                    {
                        var itemPropertyPath = $"{propertyPath}[{item.Index}]";
                        foreach (var (path, value, property) in item.Item
                                     .EnumeratePaths(currentCollectionItem, sourcePathProvider, itemPropertyPath))
                        {
                            yield return (path, value, property);
                        }
                    }
                    else
                    {
                        // TODO: Handle missing item
                    }
                }

                break;
        }
    }
}
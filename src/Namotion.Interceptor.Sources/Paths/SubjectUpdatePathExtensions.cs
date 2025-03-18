using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public static class SubjectUpdatePathExtensions
{
    // TODO: Make this extensible for path transformations and ignore callbacks

    public static SubjectUpdate? TryCreateSubjectUpdateFromPath(
        this IInterceptorSubject subject, 
        string path,
        ISourcePathProvider sourcePathProvider,
        Func<RegisteredSubjectProperty, object?> getPropertyValue)
    {
        var rootUpdate = new SubjectUpdate();
        var update = rootUpdate;
        RegisteredSubjectProperty? previousProperty = null;
        foreach (var (segment, isAttribute) in sourcePathProvider.ParsePathSegments(path))
        {
            // TODO: Use isAttribute
            
            var segmentParts = segment.Split('[', ']');
            object? index = segmentParts.Length >= 2 ? (int.TryParse(segmentParts[1], out var intIndex) ? intIndex : segmentParts[1]) : null;
            var propertyName = segmentParts[0];

            var registry = subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[subject];

            var registeredProperty = isAttribute
                ? subject.GetRegisteredAttribute(previousProperty?.Property.Name 
                    ?? throw new InvalidOperationException("Attribute segment must have a property path segment before."), segment)
                : registeredSubject.Properties[propertyName];
                
            if (sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
            {
                return null;
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

                update.Properties[propertyName] = new SubjectPropertyUpdate
                {
                    Action = SubjectPropertyUpdateAction.UpdateCollection,
                    Collection = childUpdates
                };

                update = childUpdates.Single(u => Equals(u.Index, index)).Item!;
                subject = item.Subject;
            }
            else if (registeredProperty.Type.IsAssignableTo(typeof(IInterceptorSubject))) // handle item update
            {
                var item = registeredProperty.Children.Single();
                var childUpdate = new SubjectUpdate();
                update.Properties[propertyName] = new SubjectPropertyUpdate
                {
                    Action = SubjectPropertyUpdateAction.UpdateItem,
                    Item = childUpdate
                };

                update = childUpdate;
                subject = item.Subject;
            }
            else // handle value update
            {
                update.Properties[propertyName] = new SubjectPropertyUpdate
                {
                    Action = SubjectPropertyUpdateAction.UpdateValue,
                    Value = getPropertyValue(registeredProperty),
                };
                break;
            }

            previousProperty = registeredProperty;
        }

        return rootUpdate;
    }

    public static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(this SubjectUpdate subjectUpdate,
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

    private static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(this SubjectPropertyUpdate propertyUpdate,
        IInterceptorSubject subject,
        string propertyName,
        ISourcePathProvider sourcePathProvider,
        string pathPrefix = "")
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName) ?? throw new KeyNotFoundException(propertyName);
        if (sourcePathProvider.IsPropertyIncluded(registeredProperty) == false)
        {
            yield break;
        }
        
        var propertyPath = registeredProperty.IsAttribute ? 
            sourcePathProvider.GetPropertyAttributePath(pathPrefix, registeredProperty) :
            sourcePathProvider.GetPropertyPath(pathPrefix, registeredProperty);

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

                    var currentCollectionItem = item.Index is int ? 
                        ((ICollection<IInterceptorSubject>)collection).ElementAt(Convert.ToInt32(item.Index)) : 
                        ((IDictionary)collection)[item.Index] as IInterceptorSubject;

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
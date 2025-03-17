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
        string propertyPathDelimiter, string attributePathDelimiter,
        ISourcePathProvider sourcePathProvider,
        Func<RegisteredSubjectProperty, object?> getPropertyValue)
    {
        var rootUpdate = new SubjectUpdate();
        var update = rootUpdate;
        foreach (var segment in path.Split(propertyPathDelimiter).SelectMany(a => a.Split(attributePathDelimiter)))
        {
            var segmentParts = segment.Split('[', ']');
            object? index = segmentParts.Length >= 2 ? (int.TryParse(segmentParts[1], out var intIndex) ? intIndex : segmentParts[1]) : null;
            var propertyName = segmentParts[0];

            var registry = subject.Context.GetService<ISubjectRegistry>();
            var registeredSubject = registry.KnownSubjects[subject];
            if (registeredSubject.Properties.TryGetValue(propertyName, out var registeredProperty))
            {
                if (sourcePathProvider.IsIncluded(registeredProperty) == false)
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
            }
        }

        return rootUpdate;
    }

    public static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(
        this SubjectUpdate subjectUpdate,
        IInterceptorSubject subject,
        string propertyPathDelimiter, string attributePathDelimiter,
        ISourcePathProvider sourcePathProvider)
    {
        foreach (var property in subjectUpdate.Properties)
        {
            foreach (var (path, value, registeredProperty) in property.Value
                .EnumeratePaths(property.Key, subject, property.Key, propertyPathDelimiter, attributePathDelimiter, sourcePathProvider))
            {
                yield return (path, value, registeredProperty);
            }
        }
    }

    private static IEnumerable<(string path, object? value, RegisteredSubjectProperty property)> EnumeratePaths(this SubjectPropertyUpdate propertyUpdate,
        string pathPrefix,
        IInterceptorSubject subject,
        string propertyName,
        string propertyPathDelimiter, string attributePathDelimiter,
        ISourcePathProvider sourcePathProvider)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName) ?? throw new KeyNotFoundException(propertyName);
        if (sourcePathProvider.IsIncluded(registeredProperty) == false)
        {
            yield break;
        }

        if (propertyUpdate.Attributes is not null)
        {
            foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
            {
                var registeredAttribute = subject.TryGetRegisteredAttribute(propertyName, attributeName) 
                    ?? throw new InvalidOperationException($"The attribute '{attributeName}' is not registered for property '{propertyName}'.");
            
                var attributePath = $"{pathPrefix}{propertyPathDelimiter}{attributeName}";
                foreach (var (path, value, property) in attributeUpdate.EnumeratePaths(attributePath, subject, registeredAttribute.Property.Name, propertyPathDelimiter, attributePathDelimiter, sourcePathProvider))
                {
                    yield return (path, value, property);
                }
            }
        }

        switch (propertyUpdate.Action)
        {
            case SubjectPropertyUpdateAction.UpdateValue: // handle value
                var resolvedPath = sourcePathProvider.TryGetSourcePropertyPath(pathPrefix, registeredProperty) 
                    ?? throw new InvalidOperationException($"Source path for the proposed path '{pathPrefix}' must not be null.");
         
                yield return (resolvedPath, propertyUpdate.Value, registeredProperty);
                break;

            case SubjectPropertyUpdateAction.UpdateItem: // handle item
                if (registeredProperty.GetValue() is IInterceptorSubject currentItem)
                {
                    foreach (var (path, value, property) in propertyUpdate.Item!
                        .EnumeratePaths(currentItem, propertyPathDelimiter, attributePathDelimiter, sourcePathProvider))
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
                        foreach (var (path, value, property) in item.Item
                            .EnumeratePaths(currentCollectionItem, propertyPathDelimiter, attributePathDelimiter, sourcePathProvider))
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
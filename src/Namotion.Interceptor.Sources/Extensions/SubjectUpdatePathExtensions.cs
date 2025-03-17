using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdatePathExtensions
{
    // TODO: Make this extensible for path transformations and ignore callbacks

    public static SubjectUpdate? TryCreateSubjectUpdateFromPath(
        this IInterceptorSubject subject, string path,
        string propertyPathDelimiter, string attributePathDelimiter,
        Func<RegisteredSubjectProperty, bool> isPropertyIncludedPredicate,
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
                if (isPropertyIncludedPredicate(registeredProperty) == false)
                {
                    return null;
                }

                if (index is not null) // handle array or dictionary item update
                {
                    var item = registeredProperty.Children.Single(c => Equals(c.Index, index));
                    var childUpdates = registeredProperty.Children
                        .Select(c => new SubjectPropertyCollectionUpdate
                        {
                            Index = c.Index,
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

    public static IEnumerable<(string path, object? value)> EnumeratePaths(
        this IReadOnlyDictionary<string, SubjectPropertyUpdate> propertyUpdates,
        IInterceptorSubject subject,
        string propertyPathDelimiter, string attributePathDelimiter,
        Func<RegisteredSubjectProperty, bool> isPropertyIncludedPredicate)
    {
        foreach (var property in propertyUpdates)
        {
            foreach (var (path, value) in EnumeratePaths(subject, property.Key, property.Key, property.Value, propertyPathDelimiter, attributePathDelimiter, isPropertyIncludedPredicate))
            {
                yield return (path, value);
            }
        }
    }

    private static IEnumerable<(string path, object? value)> EnumeratePaths(
        IInterceptorSubject subject, string name, string propertyName, SubjectPropertyUpdate propertyUpdate, 
        string propertyPathDelimiter, string attributePathDelimiter, 
        Func<RegisteredSubjectProperty, bool> isPropertyIncludedPredicate)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName) ?? throw new KeyNotFoundException(propertyName);
        if (isPropertyIncludedPredicate(registeredProperty) == false)
        {
            yield break;
        }
        
        if (propertyUpdate.Attributes is not null)
        {
            foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
            {
                var registeredAttribute = subject.TryGetRegisteredAttribute(propertyName, attributeName) ?? throw new KeyNotFoundException(propertyName);
                var attributePath = $"{name}{propertyPathDelimiter}{attributeName}";
                foreach (var (path, value) in EnumeratePaths(subject, attributePath, registeredAttribute.Property.Name, 
                    attributeUpdate, propertyPathDelimiter, attributePathDelimiter, isPropertyIncludedPredicate))
                {
                    yield return (path, value);
                }
            }
        }

        switch (propertyUpdate.Action)
        {
            case SubjectPropertyUpdateAction.UpdateValue: // handle value
                yield return (name, propertyUpdate.Value);
                break;

            case SubjectPropertyUpdateAction.UpdateItem: // handle item
                foreach (var (path, value) in propertyUpdate.Item!.Properties
                             .EnumeratePaths((IInterceptorSubject?)registeredProperty.GetValue()!, propertyPathDelimiter, attributePathDelimiter, isPropertyIncludedPredicate))
                {
                    yield return ($"{name}{propertyPathDelimiter}{path}", value);
                }

                break;

            case SubjectPropertyUpdateAction.UpdateCollection: // handle array or dictionary
                var collection = (IList<IInterceptorSubject>)registeredProperty.GetValue()!;
                foreach (var item in propertyUpdate.Collection!)
                {
                    if (item.Item is null)
                    {
                        continue;
                    }

                    foreach (var (path, value) in item.Item.Properties
                                 .EnumeratePaths(collection[(int)item.Index!], propertyPathDelimiter, attributePathDelimiter, isPropertyIncludedPredicate))
                    {
                        yield return ($"{name}[{item.Index}]{propertyPathDelimiter}{path}", value);
                    }
                }

                break;
        }
    }
}
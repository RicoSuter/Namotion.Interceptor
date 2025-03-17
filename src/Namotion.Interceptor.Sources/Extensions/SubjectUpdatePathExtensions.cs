using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdatePathExtensions
{
    // TODO: Make this extensible for path transformations and ignore callbacks
    
    public static SubjectUpdate? TryCreateSubjectUpdateFromPath(
        this IInterceptorSubject subject, string path, 
        string propertyPathDelimiter, string attributePathDelimiter,
        Func<RegisteredSubjectProperty, object?> getValue)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        var rootUpdate = new SubjectUpdate();
        var update = rootUpdate;
        
        foreach (var segment in path.Split(propertyPathDelimiter).SelectMany(a => a.Split(attributePathDelimiter)))
        {
            var segmentParts = segment.Split('[', ']');
            object? index = segmentParts.Length >= 2 ? (int.TryParse(segmentParts[1], out var intIndex) ? intIndex : segmentParts[1]) : null;
            var propertyName = segmentParts[0];
            
            var registeredSubject = registry.KnownSubjects[subject];
            if (registeredSubject.Properties.TryGetValue(propertyName, out var registeredProperty))
            {
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
                        Value = getValue(registeredProperty), 
                    };
                    break;
                }
            }
        }

        return rootUpdate;
    }
    
    public static IEnumerable<(string path, object? value)> EnumeratePaths(
        this IReadOnlyDictionary<string, SubjectPropertyUpdate> propertyUpdates, string propertyPathDelimiter, string attributePathDelimiter)
    {
        foreach (var property in propertyUpdates)
        {
            if (property.Value.Attributes is not null)
            {
                foreach (var (path, value) in EnumeratePaths(property.Value.Attributes, propertyPathDelimiter, attributePathDelimiter))
                {
                    yield return ($"{property.Key}{attributePathDelimiter}{path}", value);
                }
            }
            
            switch (property.Value.Action)
            {
                case SubjectPropertyUpdateAction.UpdateItem:
                    foreach (var (path, value) in EnumeratePaths(property.Value.Item!.Properties, propertyPathDelimiter, attributePathDelimiter))
                    {
                        yield return ($"{property.Key}{propertyPathDelimiter}{path}", value);
                    }
                    break;
                
                case SubjectPropertyUpdateAction.UpdateCollection:
                    foreach (var item in property.Value.Collection!)
                    {
                        if (item.Item is null)
                        {
                            continue;
                        }
                    
                        foreach (var (path, value) in EnumeratePaths(item.Item.Properties, propertyPathDelimiter, attributePathDelimiter))
                        {
                            yield return ($"{property.Key}[{item.Index}]{propertyPathDelimiter}{path}", value);
                        }
                    }
                    break;
                
                case SubjectPropertyUpdateAction.UpdateValue:
                    yield return (property.Key, property.Value.Value);
                    break;
            }
        }
    }
}
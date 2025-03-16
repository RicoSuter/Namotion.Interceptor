using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdatePathExtensions
{
    // TODO: Make this extensible for path transformations and ignore callbacks
    
    public static SubjectUpdate? TryCreateSubjectUpdateFromPath(this IInterceptorSubject subject, string path, Func<RegisteredSubjectProperty, object?> getValue, string delimiter = ".")
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();
        var rootUpdate = new SubjectUpdate();
        var update = rootUpdate;
        
        foreach (var segment in path.Split(delimiter))
        {
            var segmentParts = segment.Split('[', ']');
            object? index = segmentParts.Length >= 2 ? (int.TryParse(segmentParts[1], out var intIndex) ? intIndex : segmentParts[1]) : null;
            var propertyName = segmentParts[0];
            
            var registeredSubject = registry.KnownSubjects[subject];
            if (registeredSubject.Properties.TryGetValue(propertyName, out var property))
            {
                if (index is not null)
                {
                    var item = property.Children.Single(c => Equals(c.Index, index));
                    var childUpdates = property.Children
                        .Select(c => new SubjectPropertyCollectionUpdate
                        {
                            Index = c.Index,
                            Item = new SubjectUpdate()
                        })
                        .ToList();
                    
                    update.Properties[propertyName] = new SubjectPropertyUpdate { Items = childUpdates };
                  
                    update = childUpdates.Single(u => Equals(u.Index, index)).Item!;
                    subject = item.Subject;
                    
                }
                else if (property.Children.Any())
                {
                    var item = property.Children.Single();
                    var childUpdate = new SubjectUpdate();
                    update.Properties[propertyName] = new SubjectPropertyUpdate { Item = childUpdate };
             
                    update = childUpdate;
                    subject = item.Subject;
                }
                else
                {
                    update.Properties[propertyName] = new SubjectPropertyUpdate { Value = getValue(property) };
                    break;
                }
            }
        }

        return rootUpdate;
    }
    
    public static IEnumerable<(string path, object? value)> EnumeratePropertyPaths(
        this SubjectUpdate update, string delimiter = ".")
    {
        foreach (var property in update.Properties)
        {
            if (property.Value.Item is not null)
            {
                foreach (var (path, value) in EnumeratePropertyPaths(property.Value.Item))
                {
                    yield return ($"{property.Key}{delimiter}{path}", value);
                }
            }
            else if (property.Value.Items is not null)
            {
                foreach (var item in property.Value.Items)
                {
                    if (item.Item is null)
                    {
                        continue;
                    }
                    
                    foreach (var (path, value) in EnumeratePropertyPaths(item.Item))
                    {
                        yield return ($"{property.Key}[{item.Index}]{delimiter}{path}", value);
                    }
                }
            }
            else
            {
                yield return (property.Key, property.Value.Value);
            }
        }
    }
}
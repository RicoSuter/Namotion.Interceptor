namespace Namotion.Interceptor.Sources;

public static class SubjectUpdateExtensions
{
    public static void UpdatePropertyValueFromSource(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Item is not null)
            {
                var x = subject.Properties[propertyName].GetValue?.Invoke(subject) as IInterceptorSubject;
                if (x is not null)
                    UpdatePropertyValueFromSource(x, propertyUpdate.Item, source);
                // TODO: Implement
            }
            else if (propertyUpdate.Items is not null)
            {
                var x = subject.Properties[propertyName].GetValue?.Invoke(subject) as IEnumerable<IInterceptorSubject>;
                var i = 0;
                foreach (var item in x ?? [])
                {
                    UpdatePropertyValueFromSource(item, propertyUpdate.Items[i].Item!, source);
                    i++;
                }
                // TODO: Implement
            }
            else
            {
                var propertyReference = new PropertyReference(subject, propertyName);
                propertyReference.SetValueFromSource(source, propertyUpdate.Value);
            }
        }
    }
    
    public static IEnumerable<(string path, object? value)> EnumerateProperties(this SubjectUpdate update, string delimiter = ".")
    {
        foreach (var property in update.Properties)
        {
            if (property.Value.Item is not null)
            {
                foreach (var (path, value) in EnumerateProperties(property.Value.Item))
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
                    
                    foreach (var (path, value) in EnumerateProperties(item.Item))
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
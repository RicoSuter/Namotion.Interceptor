namespace Namotion.Interceptor;

public record struct PropertyReference
{
    private SubjectPropertyMetadata? _metadata = null;

    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }
    
    public string Name { get; }
    
    public SubjectPropertyMetadata Metadata => _metadata ?? (_metadata = Subject.Properties[Name]).Value;
    
    public object? GetValue()
    {
        return Metadata.GetValue?.Invoke(Subject);
    }
    
    public void SetValue(object? value)
    {
        Metadata.SetValue?.Invoke(Subject, value);
    }

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[$"{Name}:{key}"] = value;
    }

    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue($"{Name}:{key}", out value);
    }

    public object? GetPropertyData(string key)
    {
        return Subject.Data[$"{Name}:{key}"];
    }

    public T GetOrAddPropertyData<T>(string key, Func<T> valueFactory)
    {
        return (T)Subject.Data.GetOrAdd($"{Name}:{key}", _ => valueFactory())!;
    }
    
    public void AddOrUpdatePropertyData<T>(string key, Func<T?, T> valueFactory)
    {
        Subject.Data.AddOrUpdate($"{Name}:{key}", 
            _ => valueFactory(default), 
            (_, value) => valueFactory((T?)value));
    }
}
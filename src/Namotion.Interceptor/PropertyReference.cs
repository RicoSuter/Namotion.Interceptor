namespace Namotion.Interceptor;

public readonly record struct PropertyReference
{
    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }
    
    public string Name { get; }
    
    // TODO(perf): Cache the property metadata (?)
    public SubjectPropertyMetadata Metadata => Subject.Properties[Name];
    
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
}
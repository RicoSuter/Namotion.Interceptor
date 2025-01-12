namespace Namotion.Interceptor;

public readonly record struct PropertyReference(IInterceptorSubject Subject, string Name)
{
    public SubjectPropertyMetadata Metadata => Subject.Properties[Name];

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[$"{key}:{Name}"] = value;
    }

    public readonly bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue($"{key}:{Name}", out value);
    }

    public readonly object? GetPropertyData(string key)
    {
        return Subject.Data[$"{key}:{Name}"];
    }

    public readonly T GetOrAddPropertyData<T>(string key, Func<T> valueFactory)
    {
        return (T)Subject.Data.GetOrAdd($"{key}:{Name}", _ => valueFactory())!;
    }
}

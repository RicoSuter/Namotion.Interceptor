using Namotion.Interceptor;

namespace Namotion.Proxy;

public record struct ProxyPropertyReference(IInterceptorSubject Subject, string Name)
{
    // TODO: Rename to Info?
    public readonly SubjectPropertyInfo Metadata => Subject.Properties[Name];

    public readonly void SetPropertyData(string key, object? value)
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

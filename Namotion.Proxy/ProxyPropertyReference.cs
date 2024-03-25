namespace Namotion.Proxy;

public record struct ProxyPropertyReference(IProxy Proxy, string Name)
{
    public PropertyMetadata Metadata => Proxy.Properties[Name];

    public void SetPropertyData(string key, object? value)
    {
        Proxy.Data[$"{key}:{Name}"] = value;
    }

    public bool TryGetPropertyData(string key, out object? value)
    {
        return Proxy.Data.TryGetValue($"{key}:{Name}", out value);
    }

    public T GetOrAddPropertyData<T>(string key, Func<T> valueFactory)
    {
        return (T)Proxy.Data.GetOrAdd($"{key}:{Name}", _ => valueFactory())!;
    }
}

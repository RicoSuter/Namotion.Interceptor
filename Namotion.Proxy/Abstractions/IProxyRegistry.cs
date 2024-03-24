namespace Namotion.Proxy.Abstractions;

public interface IProxyRegistry : IProxyHandler, IObservable<ProxyPropertyChanged>
{
    IReadOnlyDictionary<IProxy, ProxyMetadata> KnownProxies { get; }
}

public record struct ProxyMetadata
{
    public ProxyMetadata()
    {
    }

    public IReadOnlyCollection<ProxyPropertyReference> Parents { get; } = new HashSet<ProxyPropertyReference>();

    public required IReadOnlyDictionary<string, ProxyProperty> Properties { get; init; }
}

public record struct ProxyProperty
{
    public ProxyProperty()
    {
    }

    public required Func<object?> GetValue { get; init; }

    public IReadOnlyCollection<ProxyPropertyChild> Children { get; } = new HashSet<ProxyPropertyChild>();
}

public record struct ProxyPropertyChild
{
    public IProxy Proxy { get; init; }

    public object? Index { get; init; }
}
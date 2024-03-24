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

    public IReadOnlyDictionary<string, ProxyProperty> Properties { get; internal set; }
}

public record struct ProxyProperty(ProxyPropertyReference Property)
{
    public required ProxyMetadata Parent { get; init; }

    public required Func<object?>? GetValue { get; init; }

    public IReadOnlyCollection<ProxyPropertyChild> Children { get; } = new HashSet<ProxyPropertyChild>();
}

public record struct ProxyPropertyChild
{
    public IProxy Proxy { get; init; }

    public object? Index { get; init; }
}
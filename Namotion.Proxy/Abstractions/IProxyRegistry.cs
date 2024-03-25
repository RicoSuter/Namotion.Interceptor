using System.Reflection;

namespace Namotion.Proxy.Abstractions;

public interface IProxyRegistry : IProxyHandler
{
    IReadOnlyDictionary<IProxy, ProxyMetadata> KnownProxies { get; }
}

public static class ProxyRegistryExtensions
{
    public static IEnumerable<ProxyProperty> GetProperties(this IProxyRegistry registry)
    {
        foreach (var pair in registry.KnownProxies)
        {
            foreach (var property in pair.Value.Properties.Values)
            {
                yield return property;
            }
        }
    }
}

public record ProxyMetadata
{
    public ProxyMetadata()
    {
    }

    public IReadOnlyCollection<ProxyPropertyReference> Parents { get; } = new HashSet<ProxyPropertyReference>();

    public IReadOnlyDictionary<string, ProxyProperty> Properties { get; internal set; }
}

public record ProxyProperty(ProxyPropertyReference Property)
{
    public required PropertyInfo Info { get; init; }

    public required ProxyMetadata Parent { get; init; }

    public required Func<object?>? GetValue { get; init; }

    public IReadOnlyCollection<ProxyPropertyChild> Children { get; } = new HashSet<ProxyPropertyChild>();
}

public readonly record struct ProxyPropertyChild
{
    public IProxy Proxy { get; init; }

    public object? Index { get; init; }
}
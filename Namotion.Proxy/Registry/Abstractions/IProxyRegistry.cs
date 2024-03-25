using Namotion.Proxy.Attributes;

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
    private readonly HashSet<ProxyPropertyReference> _parents = new();
    private readonly Dictionary<string, ProxyProperty> _properties = new();

    public required IProxy Proxy { get; init; }

    public IReadOnlyCollection<ProxyPropertyReference> Parents => _parents;

    public IReadOnlyDictionary<string, ProxyProperty> Properties => _properties;

    public void AddParent(ProxyPropertyReference parent)
    {
        lock (_parents)
        {
            _parents.Add(parent);
        }
    }

    public void RemoveParent(ProxyPropertyReference parent)
    {
        lock (_parents)
        {
            _parents.Remove(parent);
        }
    }

    public void AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params object[] attributes)
    {
        lock (_properties)
        {
            _properties.Add(name, new ProxyProperty(new ProxyPropertyReference(Proxy, name))
            {
                Parent = this,
                Type = type,
                Attributes = attributes,
                GetValue = getValue,
                SetValue = setValue
            });
        }
    }
}

public record ProxyProperty(ProxyPropertyReference Property)
{
    private readonly HashSet<ProxyPropertyChild> _children = new();

    public required Type Type { get; init; }

    public required object[] Attributes { get; init; }

    public required ProxyMetadata Parent { get; init; }

    public required Func<object?>? GetValue { get; init; }

    public required Action<object?>? SetValue { get; init; }

    public IReadOnlyCollection<ProxyPropertyChild> Children => _children;

    public void AddChild(ProxyPropertyChild parent)
    {
        lock (_children)
        {
            _children.Add(parent);
        }
    }

    public void RemoveChild(ProxyPropertyChild parent)
    {
        lock (_children)
        {
            _children.Remove(parent);
        }
    }

    public void AddAttribute(string name, Type type, Func<object?>? getValue, Action<object?>? setValue)
    {
        Parent.AddProperty(
            $"{Property.PropertyName}_{name}",
            type, getValue, setValue,
            new PropertyAttributeAttribute(Property.PropertyName, name));
    }
}

public readonly record struct ProxyPropertyChild
{
    public IProxy Proxy { get; init; }

    public object? Index { get; init; }
}
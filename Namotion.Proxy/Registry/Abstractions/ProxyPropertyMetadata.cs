using Namotion.Proxy.Registry.Attributes;
using System.Collections.Concurrent;

namespace Namotion.Proxy.Registry.Abstractions;

public record ProxyPropertyMetadata(ProxyPropertyReference Property)
{
    private readonly ConcurrentDictionary<ProxyPropertyChild, byte> _children = new();

    public required Type Type { get; init; }

    public required object[] Attributes { get; init; }

    public ProxyMetadata Parent { get; internal set; }

    public required Func<object?>? GetValue { get; init; }

    public required Action<object?>? SetValue { get; init; }

    public ICollection<ProxyPropertyChild> Children => _children.Keys;

    public void AddChild(ProxyPropertyChild parent)
    {
        _children.TryAdd(parent, 0);
    }

    public void RemoveChild(ProxyPropertyChild parent)
    {
        _children.Remove(parent, out var _);
    }

    public void AddAttribute(string name, Type type, Func<object?>? getValue, Action<object?>? setValue)
    {
        Parent.AddProperty(
            $"{Property.Name}_{name}",
            type, getValue, setValue,
            new PropertyAttributeAttribute(Property.Name, name));
    }
}

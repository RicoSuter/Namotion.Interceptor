using Namotion.Proxy.Registry.Attributes;

namespace Namotion.Proxy.Registry.Abstractions;

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

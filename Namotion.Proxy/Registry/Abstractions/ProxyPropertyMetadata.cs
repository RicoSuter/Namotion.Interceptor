using Namotion.Proxy.Attributes;

namespace Namotion.Proxy.Registry.Abstractions;

#pragma warning disable CS8618
public record ProxyPropertyMetadata(ProxyPropertyReference Property)
#pragma warning restore CS8618
{
    private readonly HashSet<ProxyPropertyChild> _children = new();

    public required Type Type { get; init; }

    public required object[] Attributes { get; init; }

    public ProxyMetadata Parent { get; internal set; }

    public required Func<object?>? GetValue { get; init; }

    public required Action<object?>? SetValue { get; init; }

    public ICollection<ProxyPropertyChild> Children
    {
        get
        {
            lock (this)
            {
                return _children.ToArray();
            }
        }
    }

    public void AddChild(ProxyPropertyChild parent)
    {
        lock (this)
            _children.Add(parent);
    }

    public void RemoveChild(ProxyPropertyChild parent)
    {
        lock (this)
            _children.Remove(parent);
    }

    public void AddAttribute(string name, Type type, Func<object?>? getValue, Action<object?>? setValue)
    {
        Parent.AddProperty(
            $"{Property.Name}_{name}",
            type, getValue, setValue,
            new PropertyAttributeAttribute(Property.Name, name));
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Namotion.Proxy.Registry.Abstractions;

public record ProxyMetadata
{
    private readonly ConcurrentDictionary<ProxyPropertyReference, byte> _parents = new();
    private readonly ConcurrentDictionary<string, ProxyPropertyMetadata> _properties = new();

    [JsonIgnore]
    public required IProxy Proxy { get; init; }

    public ICollection<ProxyPropertyReference> Parents => _parents.Keys;

    public IReadOnlyDictionary<string, ProxyPropertyMetadata> Properties => _properties;

    public void AddParent(ProxyPropertyReference parent)
    {
        _parents.TryAdd(parent, 0);
    }

    public void RemoveParent(ProxyPropertyReference parent)
    {
        _parents.Remove(parent, out var _);
    }

    public void AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params object[] attributes)
    {
        _properties.TryAdd(name, new ProxyPropertyMetadata(new ProxyPropertyReference(Proxy, name))
        {
            Parent = this,
            Type = type,
            Attributes = attributes,
            GetValue = getValue,
            SetValue = setValue
        });
    }
}

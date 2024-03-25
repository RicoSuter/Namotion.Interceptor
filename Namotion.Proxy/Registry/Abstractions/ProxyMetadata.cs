using System.Text.Json.Serialization;

namespace Namotion.Proxy.Registry.Abstractions;

public record ProxyMetadata
{
    private readonly HashSet<ProxyPropertyReference> _parents = new();
    private readonly Dictionary<string, ProxyPropertyMetadata> _properties = new();

    [JsonIgnore]
    public required IProxy Proxy { get; init; }

    public IReadOnlyCollection<ProxyPropertyReference> Parents => _parents;

    public IReadOnlyDictionary<string, ProxyPropertyMetadata> Properties => _properties;

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
            _properties.Add(name, new ProxyPropertyMetadata(new ProxyPropertyReference(Proxy, name))
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

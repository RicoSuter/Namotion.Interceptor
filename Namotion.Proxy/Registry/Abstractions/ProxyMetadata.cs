using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Namotion.Proxy.Registry.Abstractions;

public record ProxyMetadata
{
    private readonly HashSet<ProxyPropertyReference> _parents = new();
    private readonly Dictionary<string, ProxyPropertyMetadata> _properties = new Dictionary<string, ProxyPropertyMetadata>();

    [JsonIgnore]
    public IProxy Proxy { get; }

    public ProxyMetadata(IProxy proxy)
    {
        Proxy = proxy;
    }

    public ICollection<ProxyPropertyReference> Parents
    {
        get
        {
            lock (this)
                return _parents.ToArray();
        }
    }

    public IReadOnlyDictionary<string, ProxyPropertyMetadata> Properties
    {
        get
        {
            lock (this)
                return _properties.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    public void AddParent(ProxyPropertyReference parent)
    {
        lock (this)
            _parents.Add(parent);
    }

    public void RemoveParent(ProxyPropertyReference parent)
    {
        lock (this)
            _parents.Remove(parent);
    }

    public void AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params object[] attributes)
    {
        AddProperty(new ProxyPropertyMetadata(new ProxyPropertyReference(Proxy, name))
        {
            Parent = this,
            Type = type,
            Attributes = attributes,
            GetValue = getValue,
            SetValue = setValue
        });
    }

    internal void AddProperty(ProxyPropertyMetadata property)
    {
        lock (this)
        {
            _properties.Add(property.Property.Name, property);
        }
    }

    internal void AddProperties(IEnumerable<ProxyPropertyMetadata> properties)
    {
        {
            foreach (var property in properties)
            {
                _properties.Add(property.Property.Name, property);
            }
        }
    }
}

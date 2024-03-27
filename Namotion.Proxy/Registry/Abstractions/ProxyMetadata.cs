using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Namotion.Proxy.Registry.Abstractions;

public record ProxyMetadata
{
    private readonly Dictionary<string, ProxyPropertyMetadata> _properties;
    private readonly HashSet<ProxyPropertyReference> _parents = new();

    [JsonIgnore]
    public IProxy Proxy { get; }

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
                return _properties!.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    internal ProxyMetadata(IProxy proxy, IEnumerable<ProxyPropertyMetadata> properties)
    {
        Proxy = proxy;
        _properties = properties
            .ToDictionary(
                p => p.Property.Name, 
                p =>
                {
                    p.Parent = this;
                    return p;
                });
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
        lock (this)
        {
            _properties!.Add(name, new ProxyPropertyMetadata(new ProxyPropertyReference(Proxy, name))
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

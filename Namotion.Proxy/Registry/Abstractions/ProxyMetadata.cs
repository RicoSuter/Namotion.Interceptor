namespace Namotion.Proxy.Registry.Abstractions;

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

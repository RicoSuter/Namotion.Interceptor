using System.Text.Json.Serialization;
using Namotion.Interceptor;

namespace Namotion.Proxy.Registry.Abstractions;

public record RegisteredProxy
{
    private readonly object _lock = new();

    private readonly Dictionary<string, RegisteredProxyProperty> _properties;
    private readonly HashSet<PropertyReference> _parents = new();

    [JsonIgnore]
    public IInterceptorSubject Subject { get; }

    public ICollection<PropertyReference> Parents
    {
        get
        {
            lock (_lock)
                return _parents.ToArray();
        }
    }

    public IReadOnlyDictionary<string, RegisteredProxyProperty> Properties
    {
        get
        {
            lock (_lock)
                return _properties!.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    internal RegisteredProxy(IInterceptorSubject subject, IEnumerable<RegisteredProxyProperty> properties)
    {
        Subject = subject;
        _properties = properties
            .ToDictionary(
                p => p.Property.Name,
                p =>
                {
                    p.Parent = this;
                    return p;
                });
    }

    public void AddParent(PropertyReference parent)
    {
        lock (_lock)
            _parents.Add(parent);
    }

    public void RemoveParent(PropertyReference parent)
    {
        lock (_lock)
            _parents.Remove(parent);
    }

    public void AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params object[] attributes)
    {
        lock (_lock)
        {
            _properties!.Add(name, new CustomRegisteredProxyProperty(new PropertyReference(Subject, name), getValue, setValue)
            {
                Parent = this,
                Type = type,
                Attributes = attributes
            });
        }
    }
}

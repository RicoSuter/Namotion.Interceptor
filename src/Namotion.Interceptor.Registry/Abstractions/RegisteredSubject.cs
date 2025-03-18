using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Registry.Abstractions;

public record RegisteredSubject
{
    private readonly object _lock = new();

    private readonly Dictionary<string, RegisteredSubjectProperty> _properties;
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

    public IReadOnlyDictionary<string, RegisteredSubjectProperty> Properties
    {
        get
        {
            lock (_lock)
                return _properties!.ToDictionary(p => p.Key, p => p.Value);
        }
    }

    internal RegisteredSubject(IInterceptorSubject subject, IEnumerable<RegisteredSubjectProperty> properties)
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

    public void AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params Attribute[] attributes)
    {
        lock (_lock)
        {
            var property = new CustomRegisteredSubjectProperty(new PropertyReference(Subject, name), getValue, setValue)
            {
                Parent = this,
                Type = type,
                Attributes = attributes
            };
            
            _properties!.Add(name, property);
            // TODO: Raise registry changed event
        }
    }
}

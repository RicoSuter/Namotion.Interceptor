using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Registry.Abstractions;

public record RegisteredSubject
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, RegisteredSubjectProperty> _properties;
    private readonly HashSet<RegisteredSubjectProperty> _parents = new();

    [JsonIgnore]
    public IInterceptorSubject Subject { get; }

    public ICollection<RegisteredSubjectProperty> Parents
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

    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        lock (_lock)
            return _properties.GetValueOrDefault(propertyName);
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

    public void AddParent(RegisteredSubjectProperty parent)
    {
        lock (_lock)
            _parents.Add(parent);
    }

    public void RemoveParent(RegisteredSubjectProperty parent)
    {
        lock (_lock)
            _parents.Remove(parent);
    }

    public RegisteredSubjectProperty AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params Attribute[] attributes)
    {
        lock (_lock)
        {
            var reference = new PropertyReference(Subject, name);
            var property = new DynamicRegisteredSubjectProperty(reference, getValue, setValue)
            {
                Parent = this,
                Type = type,
                Attributes = attributes
            };
            
            // TODO: Raise registry changed event
            
            _properties.Add(name, property);
            return property;
        }
    }
}

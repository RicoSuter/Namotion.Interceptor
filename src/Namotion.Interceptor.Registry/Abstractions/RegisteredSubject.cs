using System.Text.Json.Serialization;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public record RegisteredSubject
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, RegisteredSubjectProperty> _properties;
    private readonly HashSet<SubjectPropertyParent> _parents = [];

    [JsonIgnore]
    public IInterceptorSubject Subject { get; }

    public ICollection<SubjectPropertyParent> Parents
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

    public void AddParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
            _parents.Add(new SubjectPropertyParent { Property = parent, Index = index });
    }

    public void RemoveParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
            _parents.Remove(new SubjectPropertyParent { Property = parent, Index = index });
    }

    public RegisteredSubjectProperty AddProperty(string name, Type type, Func<object?>? getValue, Action<object?>? setValue, params Attribute[] attributes)
    {
        var propertyReference = new PropertyReference(Subject, name);
        var property = new DynamicRegisteredSubjectProperty(propertyReference, getValue, setValue, attributes)
        {
            Parent = this,
            Type = type,
        };
        
        lock (_lock)
        {
            _properties.Add(name, property);
        }

        Subject.SetPropertyMetadata(propertyReference, new SubjectPropertyMetadata(name, type, attributes, 
            getValue is not null ? _ => getValue.Invoke() : null, 
            setValue is not null ? (_, v) => setValue.Invoke(v) : null));

        Subject.AttachSubjectProperty(propertyReference);

        return property;
    }
}

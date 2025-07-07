using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
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

    /// <summary>
    /// Adds a dynamic property with backing data to the subject.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="type">The property type.</param>
    /// <param name="getValue">The get method which is intercepted.</param>
    /// <param name="setValue">The set method which is intercepted.</param>
    /// <param name="attributes">The custom attributes.</param>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty AddProperty(string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyReference = new PropertyReference(Subject, name);
        propertyReference.SetPropertyMetadata(new SubjectPropertyMetadata(
            name,
            type,
            attributes,

            getValue is not null ? s => 
                ((IInterceptorExecutor)s.Context).GetProperty(name, () => getValue(s)) : null, 
            setValue is not null ? (s, v) => 
                ((IInterceptorExecutor)s.Context).SetProperty(name, v, 
                    getValue is not null ? () => getValue(s) : null, 
                    v2 => setValue(s, v2)) : null, 
            
            isDynamic: true));

        return AddProperty(propertyReference, type, attributes);
    }

    /// <summary>
    /// Adds a dynamic derived property to the subject with tracking of dependencies.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="type">The property type.</param>
    /// <param name="getValue">The get method which is NOT intercepted.</param>
    /// <param name="attributes">The custom attributes.</param>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty AddDerivedProperty(string name, Type type, Func<IInterceptorSubject, object?>? getValue, params Attribute[] attributes)
    {
        var propertyReference = new PropertyReference(Subject, name);
        propertyReference.SetPropertyMetadata(new SubjectPropertyMetadata(
            name, type, attributes.Concat([new DerivedAttribute()]).ToArray(),
            getValue, 
            setValue: null,
            isDynamic: true));

        return AddProperty(propertyReference, type, attributes);
    }

    private RegisteredSubjectProperty AddProperty(PropertyReference property, Type type, Attribute[] attributes)
    {
        var subjectProperty = new RegisteredSubjectProperty(property, attributes)
        {
            Parent = this,
            Type = type,
        };
        
        lock (_lock)
        {
            _properties.Add(property.Name, subjectProperty);
        }

        Subject.AttachSubjectProperty(property);
        return subjectProperty;
    }
}

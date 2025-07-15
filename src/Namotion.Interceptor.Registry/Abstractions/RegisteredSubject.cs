using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        lock (_lock)
            return _properties.GetValueOrDefault(propertyName);
    }
    
    public RegisteredSubjectProperty? TryGetRegisteredAttribute(string propertyName, string attributeName)
    {
        lock (_lock)
        {
            var attribute = _properties
                .SingleOrDefault(p => p.Value.ReflectionAttributes
                    .OfType<PropertyAttributeAttribute>()
                    .Any(a => a.PropertyName == propertyName && a.AttributeName == attributeName));

            return attribute.Value;
        }
    }

    public RegisteredSubject(IInterceptorSubject subject, IEnumerable<RegisteredSubjectProperty> properties)
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
    /// <param name="getValue">The get method.</param>
    /// <param name="setValue">The set method.</param>
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
                ((IInterceptorExecutor)s.Context).GetPropertyValue(name, () => getValue(s)) : null, 
            setValue is not null ? (s, v) => 
                ((IInterceptorExecutor)s.Context).SetPropertyValue(name, v, 
                    getValue is not null ? () => getValue(s) : null, 
                    v2 => setValue(s, v2)) : null, 
            
            isDynamic: true));

        var property = AddProperty(propertyReference, type, attributes);
        
        // trigger change event
        property.Property.SetPropertyValue(getValue?.Invoke(Subject) ?? null, 
            () => getValue?.Invoke(Subject), delegate {});
        
        return property;
    }

    /// <summary>
    /// Adds a dynamic derived property to the subject with tracking of dependencies.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="type">The property type.</param>
    /// <param name="getValue">The get method.</param>
    /// <param name="setValue">The set method.</param>
    /// <param name="attributes">The custom attributes.</param>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty AddDerivedProperty(string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        return AddProperty(name, type, getValue, setValue, attributes.Concat([new DerivedAttribute()]).ToArray());
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

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public record RegisteredSubject
{
    private readonly Lock _lock = new();

    private FrozenDictionary<string, RegisteredSubjectProperty> _properties;
    private readonly HashSet<SubjectPropertyParent> _parents = []; // TODO(perf): Use a FrozenSet?

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

    public ImmutableArray<RegisteredSubjectProperty> Properties
    {
        get
        {
            lock (_lock)
                return _properties.Values;
        }
    }

    /// <summary>
    /// Gets all attributes which are attached to this property.
    /// </summary>
    public IEnumerable<RegisteredSubjectProperty> GetPropertyAttributes(string propertyName)
    {
        lock (_lock)
        {
            return _properties.Values
                .Where(p => p.IsAttribute &&
                            p.AttributeMetadata.PropertyName == propertyName);
        }
    }

    /// <summary>
    /// Gets a property attribute by name.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    public RegisteredSubjectProperty? TryGetPropertyAttribute(string propertyName, string attributeName)
    {
        lock (_lock)
        {
            return _properties.Values
                .FirstOrDefault(p => p.IsAttribute &&
                                     p.AttributeMetadata.PropertyName == propertyName && 
                                     p.AttributeMetadata.AttributeName == attributeName);
        }
    } 

    /// <summary>
    /// Gets the property with the given name.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The property or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        lock (_lock)
            return _properties.GetValueOrDefault(propertyName);
    }

    public RegisteredSubject(IInterceptorSubject subject, IEnumerable<RegisteredSubjectProperty> properties)
    {
        Subject = subject;
        _properties = properties
            .ToFrozenDictionary(
                p => p.Name,
                p =>
                {
                    p.Parent = this;
                    return p;
                });
    }

    internal void AddParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
            _parents.Add(new SubjectPropertyParent { Property = parent, Index = index });
    }

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
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
        Subject.AddProperties(new SubjectPropertyMetadata(
            name,
            type,
            attributes,
            getValue is not null ? s => ((IInterceptorExecutor)s.Context).GetPropertyValue(name, getValue) : null, 
            setValue is not null ? (s, v) => ((IInterceptorExecutor)s.Context).SetPropertyValue(name, v, getValue, setValue) : null, 
            isIntercepted: true,
            isDynamic: true));

        var propertyReference = new PropertyReference(Subject, name);
        var property = AddProperty(propertyReference, type, attributes);
        
        // trigger change event
        property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null, 
            o => getValue?.Invoke(o), delegate {});
        
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
            _properties = _properties
                .Append(KeyValuePair.Create(subjectProperty.Name, subjectProperty))
                .ToFrozenDictionary(p => p.Key, p => p.Value);
        }

        Subject.AttachSubjectProperty(property);
        return subjectProperty;
    }
}

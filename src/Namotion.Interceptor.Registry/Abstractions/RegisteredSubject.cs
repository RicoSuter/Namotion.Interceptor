using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubject
{
    private readonly Lock _lock = new();

    private volatile FrozenDictionary<string, RegisteredSubjectProperty> _properties;
    private ImmutableArray<SubjectPropertyParent> _parents = ImmutableArray<SubjectPropertyParent>.Empty;
    
    [JsonIgnore] public IInterceptorSubject Subject { get; }

    public ImmutableArray<SubjectPropertyParent> Parents => _parents;

    public ImmutableArray<RegisteredSubjectProperty> Properties => _properties.Values;

    /// <summary>
    /// Gets all attributes which are attached to this property.
    /// </summary>
    public IEnumerable<RegisteredSubjectProperty> GetPropertyAttributes(string propertyName)
    {
        var properties = _properties.Values;
        foreach (var property in properties)
        {
            if (property.IsAttribute && property.AttributeMetadata.PropertyName == propertyName)
            {
                yield return property;
            }
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
        var properties = _properties.Values;
        foreach (var property in properties)
        {
            if (property.IsAttribute && 
                property.AttributeMetadata.PropertyName == propertyName && 
                property.AttributeMetadata.AttributeName == attributeName)
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the property with the given name.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The property or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        return _properties.GetValueOrDefault(propertyName);
    }

    public RegisteredSubject(IInterceptorSubject subject)
    {
        Subject = subject;
        _properties = subject
            .Properties
            .ToFrozenDictionary(
                p => p.Key,
                p => new RegisteredSubjectProperty(
                    this, p.Key, p.Value.Type, p.Value.Attributes));
    }

    internal void AddParent(RegisteredSubjectProperty parent, object? index)
    {
        ImmutableInterlocked.Update(ref _parents, 
            static (array, state) => array.Add(state), 
            new SubjectPropertyParent { Property = parent, Index = index });
    }

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
    { 
        ImmutableInterlocked.Update(ref _parents, 
            static (array, state) => array.Remove(state), 
            new SubjectPropertyParent { Property = parent, Index = index });
    }

    /// <summary>
    /// Adds a dynamic derived property to the subject with tracking of dependencies.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="getValue">The get method.</param>
    /// <param name="setValue">The set method.</param>
    /// <param name="attributes">The custom attributes.</param>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty AddDerivedProperty<TProperty>(string name, 
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddProperty(name, typeof(TProperty), 
            getValue is not null ? x => (TProperty)getValue(x)! : null, 
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null, 
            attributes.Concat([new DerivedAttribute()]).ToArray());
    }

    /// <summary>
    /// Adds a dynamic derived property to the subject with tracking of dependencies.
    /// </summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="getValue">The get method.</param>
    /// <param name="setValue">The set method.</param>
    /// <param name="attributes">The custom attributes.</param>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty AddProperty<TProperty>(string name, 
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddProperty(name, typeof(TProperty), 
            getValue is not null ? x => (TProperty)getValue(x)! : null, 
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null, 
            attributes);
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
    public RegisteredSubjectProperty AddDerivedProperty(string name, 
        Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddProperty(name, type, getValue, setValue, attributes
            .Concat([new DerivedAttribute()]).ToArray());
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
    public RegisteredSubjectProperty AddProperty(
        string name, 
        Type type,
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

        var property = AddPropertyInternal(name, type, attributes);

        // trigger change event
        property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null,
            o => getValue?.Invoke(o), delegate { });

        return property;
    }

    private RegisteredSubjectProperty AddPropertyInternal(string name, Type type, Attribute[] attributes)
    {
        var subjectProperty = new RegisteredSubjectProperty(this, name, type, attributes);

        lock (_lock)
        {
            var properties = _properties;
            properties = properties
                .Append(KeyValuePair.Create(subjectProperty.Name, subjectProperty))
                .ToFrozenDictionary(p => p.Key, p => p.Value);

            _properties = properties;
            foreach (var property in _properties.Values)
            {
                property.AttributesCache = null;
            }
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);
        return subjectProperty;
    }
}

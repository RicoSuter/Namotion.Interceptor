﻿using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public record RegisteredSubjectProperty
{
    private readonly HashSet<SubjectPropertyChild> _children = [];
    private readonly PropertyAttributeAttribute? _attributeMetadata;

    public RegisteredSubjectProperty(PropertyReference property, IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        Property = property;
        ReflectionAttributes = reflectionAttributes;
        _attributeMetadata = reflectionAttributes.OfType<PropertyAttributeAttribute>().SingleOrDefault();
    }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public required Type Type { get; init; }
    
    public PropertyReference Property { get; }

    /// <summary>
    /// Gets a list of all .NET reflection attributes.
    /// </summary>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; }
    
    /// <summary>
    /// Gets the browse name of the property (either the property or attribute name).
    /// </summary>
    public string BrowseName => IsAttribute ? AttributeMetadata.AttributeName : Property.Name;
    
    /// <summary>
    /// Specifies whether the property is an attribute property (property attached to another property).
    /// </summary>
    public bool IsAttribute => ReflectionAttributes.Any(a => a is PropertyAttributeAttribute);
    
    /// <summary>
    /// Gets the attribute with information about this attribute property.
    /// </summary>
    public PropertyAttributeAttribute AttributeMetadata => _attributeMetadata 
        ?? throw new InvalidOperationException("The property is not an attribute.");

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference => Type.IsAssignableTo(typeof(IInterceptorSubject));

    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a collection.
    /// </summary>
    public bool IsSubjectCollection => Type.IsAssignableTo(typeof(IEnumerable<IInterceptorSubject>));

    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary => Type.IsAssignableTo(typeof(IReadOnlyDictionary<string, IInterceptorSubject?>));

    /// <summary>
    /// Gets the parent subject which contains the property.
    /// </summary>
    public RegisteredSubject Parent { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the property has a getter.
    /// </summary>
    public bool HasGetter => Property.Metadata.GetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter.
    /// </summary>
    public bool HasSetter => Property.Metadata.SetValue is not null;

    /// <summary>
    /// Checks whether this is an attribute property for the given property.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The result.</returns>
    public bool IsAttributeForProperty(string propertyName) 
        => ReflectionAttributes.OfType<PropertyAttributeAttribute>().Any(a => a.PropertyName == propertyName);

    /// <summary>
    /// Gets the current value of the property.
    /// </summary>
    /// <returns>The value.</returns>
    public object? GetValue()
    {
        return Property.Metadata.GetValue?.Invoke(Property.Subject);
    }

    /// <summary>
    /// Sets the value of the property.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetValue(object? value)
    {
        Property.Metadata.SetValue?.Invoke(Property.Subject, value);
    }

    /// <summary>
    /// Gets the collection or dictionary items of the property.
    /// </summary>
    public ICollection<SubjectPropertyChild> Children
    {
        get
        {
            lock (this)
            {
                return _children.ToArray();
            }
        }
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute(
        string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyName = $"{Property.Name}@{name}";
        
        var attribute = Parent.AddProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Property.Name, name)])
                .ToArray());

        return attribute;
    }

    /// <summary>
    /// Adds a derived attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddDerivedAttribute(
        string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyName = $"{Property.Name}@{name}";
        
        var attribute = Parent.AddDerivedProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Property.Name, name)])
                .ToArray());

        return attribute;
    }

    /// <summary>
    /// Gets a property attribute by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    public RegisteredSubjectProperty? TryGetAttribute(string attributeName)
    {
        var pair = Parent.Properties
            .SingleOrDefault(p => p.Value.ReflectionAttributes
                .OfType<PropertyAttributeAttribute>()
                .Any(a => a.PropertyName == Property.Name && a.AttributeName == attributeName));

        return pair.Value;
    } 

    /// <summary>
    /// Gets the attribute property this property is attached to.
    /// </summary>
    /// <returns>The property.</returns>
    public RegisteredSubjectProperty GetAttributedProperty()
    {
        return Parent.Properties
            .Single(p => p.Key == AttributeMetadata.PropertyName)
            .Value;
    }

    public static implicit operator PropertyReference(RegisteredSubjectProperty property)
    {
        return property.Property;
    }

    internal void AddChild(SubjectPropertyChild parent)
    {
        lock (this)
            _children.Add(parent);
    }

    internal void RemoveChild(SubjectPropertyChild parent)
    {
        lock (this)
            _children.Remove(parent);
    }
}

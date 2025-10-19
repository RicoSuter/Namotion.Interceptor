using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectReferenceCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectCollectionCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectDictionaryCache = new();

    private readonly List<SubjectPropertyChild> _children = [];
    private readonly PropertyAttributeAttribute? _attributeMetadata;

    public RegisteredSubjectProperty(RegisteredSubject parent, string name, 
        Type type, IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        Parent = parent;
        Type = type;
        ReflectionAttributes = reflectionAttributes;
        Reference = new PropertyReference(parent.Subject, name);

        _attributeMetadata = reflectionAttributes.OfType<PropertyAttributeAttribute>().SingleOrDefault();
    }

    /// <summary>
    /// Gets the subject object this property belongs to.
    /// </summary>
    public IInterceptorSubject Subject => Reference.Subject;

    /// <summary>
    /// Gets the name of the property.
    /// </summary>
    public string Name => Reference.Name;

    /// <summary>
    /// Gets the parent subject which contains the property.
    /// </summary>
    public RegisteredSubject Parent { get; }
    
    /// <summary>
    /// Gets the property reference.
    /// </summary>
    public PropertyReference Reference { get; }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets a list of all .NET reflection attributes.
    /// </summary>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; }
    
    /// <summary>
    /// Gets the browse name of the property (either the property or attribute name).
    /// </summary>
    public string BrowseName => IsAttribute ? AttributeMetadata.AttributeName : Name;
    
    /// <summary>
    /// Specifies whether the property is an attribute property (property attached to another property).
    /// </summary>
    public bool IsAttribute => _attributeMetadata is not null;

    /// <summary>
    /// Gets the attribute with information about this attribute property.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    public PropertyAttributeAttribute AttributeMetadata => _attributeMetadata 
        ?? throw new InvalidOperationException("The property is not an attribute.");
    
    /// <summary>
    /// Checks whether this property has child subjects, which can be either
    /// a subject reference, a collection of subjects, or a dictionary of subjects.
    /// </summary>
    public bool HasChildSubjects => 
        IsSubjectReference || IsSubjectCollection || IsSubjectDictionary;

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference => 
        IsSubjectReferenceCache.GetOrAdd(Type, t => 
            t == typeof(object) || t.IsAssignableTo(typeof(IInterceptorSubject)));
    
    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a collection.
    /// </summary>
    public bool IsSubjectCollection =>
        IsSubjectCollectionCache.GetOrAdd(Type, t =>
        {
            return
                t.IsAssignableTo(typeof(IEnumerable)) &&
                t.GetInterfaces().Any(i =>
                    i.IsAssignableTo(typeof(IEnumerable)) &&
                    i.GenericTypeArguments.FirstOrDefault()?.IsAssignableTo(typeof(IInterceptorSubject)) == true);
        });

    /// <summary>
    /// Gets a value indicating whether this property references multiple subject with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary =>
        IsSubjectDictionaryCache.GetOrAdd(Type, t =>
        {
            return
                t.IsAssignableTo(typeof(IEnumerable)) && 
                t.GetInterfaces().Any(i => 
                    i.IsAssignableTo(typeof(IEnumerable)) && 
                    i.GenericTypeArguments.FirstOrDefault() is
                    {
                        Name: "KeyValuePair`2",
                        Namespace: "System.Collections.Generic"
                    } keyValueType && keyValueType.GenericTypeArguments[1].IsAssignableTo(typeof(IInterceptorSubject)));
        });

    /// <summary>
    /// Gets a value indicating whether the property has a getter.
    /// </summary>
    public bool HasGetter => Reference.Metadata.GetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter.
    /// </summary>
    public bool HasSetter => Reference.Metadata.SetValue is not null;

    /// <summary>
    /// Gets the current value of the property.
    /// </summary>
    /// <returns>The value.</returns>
    public object? GetValue()
    {
        return Reference.Metadata.GetValue?.Invoke(Subject);
    }

    /// <summary>
    /// Sets the value of the property.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetValue(object? value)
    {
        Reference.Metadata.SetValue?.Invoke(Subject, value);
    }

    /// <summary>
    /// Gets the collection or dictionary items of the property.
    /// </summary>
    public ICollection<SubjectPropertyChild> Children
    {
        get
        {
            lock (_children)
            {
                return _children.ToArray();
            }
        }
    }
    
    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), 
            getValue is not null ? x => (TProperty)getValue(x)! : null, 
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null, 
            attributes);
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), getValue, setValue, attributes);
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
        var propertyName = $"{Name}@{name}";
        
        var attribute = Parent.AddProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Name, name)])
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
        var propertyName = $"{Name}@{name}";
        
        var attribute = Parent.AddDerivedProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Name, name)])
                .ToArray());

        return attribute;
    }

    /// <summary>
    /// Gets all attributes which are attached to this property.
    /// </summary>
    public RegisteredSubjectProperty[] Attributes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AttributesCache = (AttributesCache ?? Parent.GetPropertyAttributes(Name).ToArray());
    }
    
    internal RegisteredSubjectProperty[]? AttributesCache = null; // TODO: Dangerous cache, needs review

    /// <summary>
    /// Gets a property attribute by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetAttribute(string attributeName)
    {
        return Parent.TryGetPropertyAttribute(Name, attributeName);
    } 

    /// <summary>
    /// Gets the attribute property this property is attached to.
    /// </summary>
    /// <returns>The property.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the property this attribute is attached could not be found.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty GetAttributedProperty()
    {
        return Parent.TryGetProperty(AttributeMetadata.PropertyName) ??
            throw new InvalidOperationException($"The attributed property '{AttributeMetadata.PropertyName}' could not be found on the parent subject.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator PropertyReference(RegisteredSubjectProperty property)
    {
        return property.Reference;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            if (!_children.Contains(child))
            {
                _children.Add(child);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RemoveChild(SubjectPropertyChild parent)
    {
        lock (_children)
        {
            var index = _children.IndexOf(parent);
            if (index == -1)
            {
                return;
            }

            _children.RemoveAt(index);

            if (IsSubjectCollection && index < _children.Count)
            {
                for (int i = index; i < _children.Count; i++)
                {
                    var child = _children[i];
                    _children[i] = child with { Index = i };
                }
            }
        }
    }
}

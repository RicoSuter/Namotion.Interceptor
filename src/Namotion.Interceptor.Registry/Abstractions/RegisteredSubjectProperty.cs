using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Performance;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    private static readonly ObjectPool<RegisteredSubjectProperty> Pool = new(
        static () => new RegisteredSubjectProperty());

    private static readonly ConcurrentDictionary<Type, bool> IsSubjectReferenceCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectCollectionCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectDictionaryCache = new();

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    private PropertyAttributeAttribute? _attributeMetadata;
    internal RegisteredSubjectProperty[]? AttributesCache = null; // TODO: Dangerous cache, needs review

    private RegisteredSubjectProperty()
    {
    }

    public static RegisteredSubjectProperty Create(RegisteredSubject parent, string name, Type type, IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        var property = Pool.Rent();
        property.Parent = parent;
        property.Type = type;
        property.ReflectionAttributes = reflectionAttributes;
        property.Reference = new PropertyReference(parent.Subject, name);

        // Find PropertyAttributeAttribute without LINQ allocation
        property._attributeMetadata = null;
        foreach (var attribute in reflectionAttributes)
        {
            if (attribute is PropertyAttributeAttribute paa)
            {
                property._attributeMetadata = paa;
                break;
            }
        }

        return property;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return()
    {
        // No need to lock because the subject containing this property is removed from the registry already
        // ReSharper disable once InconsistentlySynchronizedField
        _children.Clear();
        _childrenCache = default;
        AttributesCache = null;

        // Clear all references to allow GC and prevent use-after-return issues
        Parent = null!;
        Reference = default;
        Type = null!;
        ReflectionAttributes = null!;
        _attributeMetadata = null;

        Pool.Return(this);
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
    public RegisteredSubject Parent { get; private set; }
    
    /// <summary>
    /// Gets the property reference.
    /// </summary>
    public PropertyReference Reference { get; private set; }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; private set; }

    /// <summary>
    /// Gets a list of all .NET reflection attributes.
    /// </summary>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; private set; }
    
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
    public bool HasChildSubjects => IsSubjectReference || IsSubjectCollection || IsSubjectDictionary;

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference => IsSubjectReferenceCache.GetOrAdd(Type, IsSubjectReferenceType);
    
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
                    IsSubjectReferenceType(i.GenericTypeArguments.FirstOrDefault()));
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
                    } keyValueType && IsSubjectReferenceType(keyValueType.GenericTypeArguments[1]));
        });

    private static bool IsSubjectReferenceType(Type? type)
    {
        if (type is null) return false;
        return type.IsInterface || // any subject type might implement an any interface
               type == typeof(object) ||
               type.IsAssignableTo(typeof(IInterceptorSubject));
    }

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
    /// Thread-safe: Lock on private readonly List ensures thread-safe access.
    /// Performance: Returns cached ImmutableArray - only rebuilds when invalidated.
    /// </summary>
    public ImmutableArray<SubjectPropertyChild> Children
    {
        get
        {
            lock (_children)
            {
                if (_childrenCache.IsDefault)
                {
                    _childrenCache = [.. _children];
                }

                return _childrenCache;
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
            // No Contains check needed - LifecycleInterceptor already guarantees
            // no duplicates via HashSet<PropertyReference?> in _attachedSubjects
            _children.Add(child);
            _childrenCache = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RemoveChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            var index = _children.IndexOf(child);
            if (index == -1)
                return;

            _children.RemoveAt(index);

            // Handle collection index reordering after removal
            // For subject collections, update all further indices
            if (IsSubjectCollection && index < _children.Count)
            {
                for (int i = index; i < _children.Count; i++)
                {
                    _children[i] = _children[i] with { Index = i };
                }
            }

            _childrenCache = default; // invalidate cache
        }
    }
}

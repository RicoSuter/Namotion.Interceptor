using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableChildPositions;

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    private readonly PropertyAttributeAttribute? _attributeMetadata;

    // 0: not computed yet, 1: no public setter, 2: public setter. Computed on first read because it reflects over the setter.
    private byte _hasPublicSetter;

    internal RegisteredSubjectProperty[]? AttributesCache = null; // TODO: Dangerous cache, needs review

    public RegisteredSubjectProperty(RegisteredSubject parent, string name,
        Type type, IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        Parent = parent;
        Type = type;
        ReflectionAttributes = reflectionAttributes;
        Reference = new PropertyReference(parent.Subject, name);

        foreach (var attribute in reflectionAttributes)
        {
            if (attribute is PropertyAttributeAttribute paa)
            {
                _attributeMetadata = paa;
                break;
            }
        }
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
    /// Gets all .NET reflection attributes for this property, including inherited attributes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This collection includes attributes from multiple sources in the following order:
    /// </para>
    /// <list type="number">
    ///   <item>Attributes declared directly on the class property (and inherited from base classes)</item>
    ///   <item>Attributes from implemented interface properties (matched by name)</item>
    /// </list>
    /// <para>
    /// The inheritance rules mirror .NET's class inheritance behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item>If an attribute has <c>AllowMultiple=false</c> and exists on both the class
    ///         and interface, only the class attribute is included (class wins)</item>
    ///   <item>If an attribute has <c>AllowMultiple=true</c>, attributes from both class
    ///         and interfaces are included</item>
    ///   <item>Interface attributes are collected in interface declaration order</item>
    /// </list>
    /// </remarks>
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
    /// Checks whether this is a structural property, one that holds child subjects: a subject reference,
    /// a collection of subjects, or a dictionary of subjects.
    /// </summary>
    public bool CanContainSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.CanContainSubjects();
    }

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectReferenceType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a collection.
    /// </summary>
    public bool IsSubjectCollection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectCollectionType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectDictionaryType();
    }

    /// <summary>
    /// Gets a value indicating whether the property has a getter.
    /// </summary>
    public bool HasGetter => Reference.Metadata.GetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter of any accessibility, see <see cref="HasPublicSetter"/>;
    /// a generated init-only property has no setter.
    /// </summary>
    public bool HasSetter => Reference.Metadata.SetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter that is public. An init-only setter does not count as public.
    /// A setter that an override inherits has the accessibility of the property it overrides. An explicit or default
    /// interface implementation has the accessibility of the implemented interface member. A dynamic property without
    /// <see cref="SubjectPropertyMetadata.PropertyInfo"/> counts as public when it has a setter.
    /// </summary>
    // TODO: Read the accessibility from the generated metadata instead of the PropertyInfo (see #732)
    public bool HasPublicSetter
    {
        get
        {
            var hasPublicSetter = _hasPublicSetter;
            if (hasPublicSetter == 0)
            {
                hasPublicSetter = HasSetter && IsSetterPublic(Reference.Metadata.PropertyInfo) ? (byte)2 : (byte)1;
                _hasPublicSetter = hasPublicSetter;
            }

            return hasPublicSetter == 2;
        }
    }

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

    internal void ClearChildren()
    {
        lock (_children)
        {
            _children.Clear();
            _childrenCache = default;
        }
    }

    internal void AddChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            // No Contains check needed - LifecycleInterceptor attaches a subject to a property once.
            _children.Insert(GetInsertPosition(child.Index), child);
            _childrenCache = default;
        }
    }

    internal void RemoveChild(IInterceptorSubject subject)
    {
        lock (_children)
        {
            var position = IndexOfChild(subject);
            if (position >= 0)
            {
                _children.RemoveAt(position);
                _childrenCache = default;
            }
        }
    }

    /// <summary>
    /// Gives moved children and their parent entries the key or position they have in the value now.
    /// Must be called inside the registry's _knownSubjects lock, which every change to children and
    /// parent entries takes first.
    /// </summary>
    /// <param name="children">The children that moved, with their new key or position.</param>
    /// <param name="knownSubjects">The registered subjects, to find the parent entries of the children.</param>
    internal void UpdateChildIndices(ReadOnlySpan<SubjectChild> children, Dictionary<IInterceptorSubject, RegisteredSubject> knownSubjects)
    {
        lock (_children)
        {
            // A lookup pays off from the second child on, since a moved collection item usually
            // shifts the rest.
            Dictionary<IInterceptorSubject, int>? positions = null;
            if (children.Length > 1)
            {
                positions = _reusableChildPositions ??= new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < _children.Count; i++)
                {
                    positions[_children[i].Subject] = i;
                }
            }

            foreach (var child in children)
            {
                var position = positions?.GetValueOrDefault(child.Subject, -1) ?? IndexOfChild(child.Subject);
                if (position < 0)
                    continue;

                _children[position] = _children[position] with { Index = child.Index };
                knownSubjects.GetValueOrDefault(child.Subject)?.UpdateParentIndex(this, child.Index);
            }

            SortByPosition();
            _childrenCache = default;

            // Release references so subjects can be GC'd on idle threads. Nothing above runs user code or
            // throws; a throw would leave entries of this property in the thread-static map for the next one.
            positions?.Clear();
        }
    }

    // Searches backwards because a reconciled write detaches in reverse order, which finds each
    // child removed from the end of the value at the end of the list.
    private int IndexOfChild(IInterceptorSubject subject)
    {
        for (var i = _children.Count - 1; i >= 0; i--)
        {
            if (_children[i].Subject == subject)
                return i;
        }

        return -1;
    }

    // Children of a collection are kept in position order, which an append leaves as it is. A
    // property declared as a dictionary is not reordered by key, so its children stay in
    // the order they were attached, whatever the type of their keys.
    private int GetInsertPosition(object? index)
    {
        var position = _children.Count;
        if (index is int newPosition && !IsSubjectDictionary)
        {
            while (position > 0 &&
                   _children[position - 1].Index is int previousPosition &&
                   previousPosition > newPosition)
            {
                position--;
            }
        }

        return position;
    }

    private void SortByPosition()
    {
        if (IsSubjectDictionary)
            return;

        foreach (var child in _children)
        {
            if (child.Index is not int)
                return;
        }

        _children.Sort(static (a, b) => ((int)a.Index!).CompareTo((int)b.Index!));
    }

    private static bool IsSetterPublic(PropertyInfo? propertyInfo)
    {
        if (propertyInfo is null)
        {
            return true;
        }

        // An override that declares only the getter has no SetMethod of its own; the root declaration holds the setter.
        var setter = propertyInfo.SetMethod;
        if (setter is null && propertyInfo.GetMethod?.GetBaseDefinition().DeclaringType is { } rootType)
        {
            setter = rootType
                .GetProperty(propertyInfo.Name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .SetMethod;
        }

        return setter is { IsPublic: true } && !IsInitOnly(setter);
    }

    private static bool IsInitOnly(MethodInfo setter)
    {
        // Matched by name: an assembly built for an older framework declares its own IsExternalInit.
        foreach (var modifier in setter.ReturnParameter.GetRequiredCustomModifiers())
        {
            if (modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit")
            {
                return true;
            }
        }

        return false;
    }
}

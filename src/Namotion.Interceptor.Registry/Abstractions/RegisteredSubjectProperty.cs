using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableCollectionPositions;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, object?>? _reusableDisplacedChildren;

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

    internal void RemoveChild(IInterceptorSubject subject)
    {
        lock (_children)
        {
            // Matched by subject alone: a property lists a subject once however many positions
            // or keys it sits under, and the index a detach reports need not be the registered
            // one. Search backwards because a reconciled write detaches in reverse order, which
            // finds each child at the end of the list.
            for (var i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].Subject == subject)
                {
                    _children.RemoveAt(i);
                    _childrenCache = default;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Syncs children's indices and parent entries with the live collection or dictionary.
    /// Must be called while LifecycleInterceptor's _attachedSubjects lock is held,
    /// because this method acquires _children then _knownSubjects, which is the inverse of
    /// HandleLifecycleChange's lock order. The outer _attachedSubjects lock serializes
    /// both paths and prevents deadlock.
    /// </summary>
    /// <param name="collectionValue">The current collection or dictionary value (passed from caller to avoid re-reading through interceptors).</param>
    /// <param name="registry">The subject registry (passed from caller to avoid repeated service resolution per child).</param>
    internal void RefreshCollectionIndices(object? collectionValue, ISubjectRegistry registry)
    {
        // Dispatches on the value in the order LifecycleInterceptor.FindSubjectsInProperty
        // does, whatever the property declares, because that scan decided whether each child
        // was attached under a key or a position.
        switch (collectionValue)
        {
            case null or IInterceptorSubject or string:
                return;

            case IDictionary:
                RefreshDictionaryKeys(collectionValue, registry);
                return;

            case ICollection:
                RefreshCollectionPositions(collectionValue, registry);
                return;

            case IEnumerable when IsSubjectDictionary || collectionValue.GetType().IsSubjectDictionaryType():
                RefreshDictionaryKeys(collectionValue, registry);
                return;

            case IEnumerable:
                RefreshCollectionPositions(collectionValue, registry);
                return;
        }
    }

    private void RefreshCollectionPositions(object? collectionValue, ISubjectRegistry registry)
    {
        lock (_children)
        {
            var collectionPositions = BuildCollectionPositions(collectionValue, _children.Count);
            if (collectionPositions is null)
                return;

            for (var i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                if (!collectionPositions.TryGetValue(child.Subject, out var newIndex))
                    continue;

                // Compare unboxed to avoid allocating a boxed int when index hasn't changed
                if (child.Index is int oldIndex && oldIndex == newIndex)
                    continue;

                var boxedNewIndex = (object)newIndex;
                _children[i] = child with { Index = boxedNewIndex };

                registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, boxedNewIndex);
            }

            // Sort children to match live collection order
            _children.Sort(static (a, b) => ((int)a.Index!).CompareTo((int)b.Index!));
            _childrenCache = default;

            // Release references so subjects can be GC'd on idle threads
            collectionPositions.Clear();
        }
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

    private void RefreshDictionaryKeys(object dictionaryValue, ISubjectRegistry registry)
    {
        lock (_children)
        {
            var position = 0;
            Dictionary<IInterceptorSubject, object?>? displacedChildren = null;
            try
            {
                if (dictionaryValue is IDictionary dictionary)
                {
                    // IDictionaryEnumerator hands out key and value separately, where foreach
                    // would box a DictionaryEntry per entry.
                    var enumerator = dictionary.GetEnumerator();
                    try
                    {
                        while (enumerator.MoveNext())
                        {
                            if (enumerator.Value is IInterceptorSubject subject)
                            {
                                RefreshDictionaryChild(subject, enumerator.Key, ref position, ref displacedChildren, registry);
                            }
                        }
                    }
                    finally
                    {
                        (enumerator as IDisposable)?.Dispose();
                    }
                }
                else
                {
                    var index = 0;
                    foreach (var item in (IEnumerable)dictionaryValue)
                    {
                        if (item is not null && SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var keyedSubject))
                        {
                            RefreshDictionaryChild(keyedSubject, key, ref position, ref displacedChildren, registry);
                        }
                        else if (item is IInterceptorSubject subject)
                        {
                            // A dictionary type that enumerates as its values is attached by position.
                            RefreshDictionaryChild(subject, index, ref position, ref displacedChildren, registry);
                        }

                        index++;
                    }
                }
            }
            finally
            {
                if (displacedChildren is not null)
                {
                    // Still displaced is a child the walk did not reach, because an exception cut
                    // it off or the value does not hold it. It must not be lost.
                    foreach (var displaced in displacedChildren)
                    {
                        _children.Add(new SubjectPropertyChild { Subject = displaced.Key, Index = displaced.Value });
                    }

                    // Release references so subjects can be GC'd on idle threads
                    displacedChildren.Clear();
                    _reusableDisplacedChildren = displacedChildren;
                }
            }
        }
    }

    /// <summary>
    /// Gives the child of the next dictionary entry that entry's key and, where the children are
    /// not in enumeration order, its place in it. A subject under several keys keeps the first,
    /// which is the one LifecycleInterceptor attaches it with.
    /// Must be called under the _children lock.
    /// </summary>
    private void RefreshDictionaryChild(IInterceptorSubject subject, object? key, ref int position,
        ref Dictionary<IInterceptorSubject, object?>? displacedChildren, ISubjectRegistry registry)
    {
        if (displacedChildren is null)
        {
            // Every child is placed, so this is a further key of one of them or a subject
            // the property does not list.
            if (position == _children.Count)
                return;

            if (_children[position].Subject != subject)
            {
                // Children sit in enumeration order as long as entries are only appended or
                // removed, and are then matched without a lookup. Once an entry breaks that
                // order the remaining children are taken out and put back by lookup, which
                // restores the order for the next refresh.
                //
                // The map is taken out of its thread-static slot while in use and put back by
                // the caller, so that a refresh nested on this thread, which a dictionary that
                // writes a property while it is enumerated can cause, gets a map of its own.
                displacedChildren = _reusableDisplacedChildren ?? new Dictionary<IInterceptorSubject, object?>(ReferenceEqualityComparer.Instance);
                _reusableDisplacedChildren = null;
                for (var i = position; i < _children.Count; i++)
                {
                    displacedChildren[_children[i].Subject] = _children[i].Index;
                }

                _children.RemoveRange(position, _children.Count - position);
                _childrenCache = default;
            }
        }

        if (displacedChildren is not null)
        {
            // Not found for a further key of a subject that is already placed, or for a
            // subject the property does not list.
            if (!displacedChildren.Remove(subject, out var displacedKey))
                return;

            // Put back under its old key first, so a throwing key comparison below cannot lose it.
            _children.Add(new SubjectPropertyChild { Subject = subject, Index = displacedKey });
        }

        var child = _children[position];
        position++;

        // Reference-equal keys return before any Equals override runs, and a retained entry
        // usually carries the same key object.
        if (Equals(child.Index, key))
            return;

        _children[position - 1] = child with { Index = key };
        _childrenCache = default;

        registry.TryGetRegisteredSubject(subject)?.UpdateParentIndex(this, key);
    }

    /// <summary>
    /// Maps each subject in the collection to its current position, the first one for a subject
    /// listed more than once, which is the one LifecycleInterceptor attaches it with.
    /// Uses IList indexed access when available; falls back to ICollection foreach,
    /// then IEnumerable for read-only types that implement neither.
    /// Reuses a ThreadStatic dictionary to avoid allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<IInterceptorSubject, int>? BuildCollectionPositions(object? value, int capacityHint)
    {
        if (value is null)
            return null;

        var collectionPositions = _reusableCollectionPositions;
        collectionPositions?.Clear();

        if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions.TryAdd(subject, index);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var index = 0;
            foreach (var item in collection)
            {
                if (item is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions.TryAdd(subject, index);
                }
                index++;
            }
        }
        else if (value is IEnumerable enumerable and not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions.TryAdd(subject, index);
                }
                index++;
            }
        }

        return collectionPositions;
    }
}

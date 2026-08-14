using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableCollectionPositions;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, object?>? _reusableDictionaryKeys;

    private const byte ContainerKindUnresolved = 0;
    private const byte ContainerKindNone = 1;
    private const byte ContainerKindReference = 2;
    private const byte ContainerKindCollection = 3;
    private const byte ContainerKindDictionary = 4;

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    private byte _containerKind;

    private readonly PropertyAttributeAttribute? _attributeMetadata;

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
    /// Checks whether this property has child subjects, which can be either
    /// a subject reference, a collection of subjects, or a dictionary of subjects.
    /// </summary>
    public bool CanContainSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ContainerKind != ContainerKindNone;
    }

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ContainerKind == ContainerKindReference;
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a collection.
    /// </summary>
    public bool IsSubjectCollection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ContainerKind == ContainerKindCollection;
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ContainerKind == ContainerKindDictionary;
    }

    /// <summary>
    /// The property's container classification. Reference, collection and dictionary are mutually exclusive,
    /// and <see cref="CanContainSubjects"/> is their union, so one resolved byte answers all four predicates
    /// without the per-call type lookup each of them used to do.
    /// </summary>
    private byte ContainerKind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var containerKind = _containerKind;
            return containerKind != ContainerKindUnresolved ? containerKind : ResolveContainerKind();
        }
    }

    private byte ResolveContainerKind()
    {
        // The union first, so a property that cannot hold a subject, which is most of them, resolves in one
        // lookup. Reference then needs no lookup of its own: it is whatever the other two are not.
        var containerKind =
            !Type.CanContainSubjects() ? ContainerKindNone :
            Type.IsSubjectCollectionType() ? ContainerKindCollection :
            Type.IsSubjectDictionaryType() ? ContainerKindDictionary :
            ContainerKindReference;

        // Racy on purpose: every thread resolves the same value from an immutable Type, and a byte
        // assignment is atomic, so the worst case is resolving twice.
        _containerKind = containerKind;
        return containerKind;
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

    internal void RemoveChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            // Matched by subject alone, for every property kind: attach adds at most one child per subject,
            // and the stored index can differ from the one this detach carries, which would leave the child
            // behind. Search backwards because LifecycleInterceptor detaches in reverse collection order,
            // making each lookup O(1) instead of O(n).
            var subject = child.Subject;
            var index = -1;
            for (var i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].Subject == subject)
                {
                    index = i;
                    break;
                }
            }

            if (index == -1)
                return;

            _children.RemoveAt(index);
            _childrenCache = default;
        }
    }

    /// <summary>
    /// Syncs children's indices and parent entries with the live collection or dictionary.
    /// Must be called while LifecycleInterceptor's _attachedSubjects lock is held,
    /// because this method acquires _children then _knownSubjects, which is the inverse of
    /// HandleLifecycleChange's lock order. The outer _attachedSubjects lock serializes
    /// both paths and prevents deadlock.
    /// </summary>
    /// <param name="value">The current collection or dictionary value (passed from caller to avoid re-reading through interceptors).</param>
    /// <param name="registry">The subject registry (passed from caller to avoid repeated service resolution per child).</param>
    internal void RefreshChildIndices(object? value, ISubjectRegistry registry)
    {
        switch (ContainerKind)
        {
            case ContainerKindCollection:
                RefreshCollectionPositions(value, registry);
                return;

            // Dictionary children are keyed, so reordering entries leaves their stored Index alone, but
            // moving a retained subject to another key does change it.
            case ContainerKindDictionary:
                RefreshDictionaryKeys(value, registry);
                return;

            // Declared as object or as a plain interface, so it can hold a container too. The lifecycle
            // interceptor derives these children's indices from the value, so this has to as well.
            case ContainerKindReference:
                switch (value)
                {
                    // The interceptor keys a subject value as a whole, with a null index.
                    case IInterceptorSubject:
                    case IDictionary:
                        RefreshDictionaryKeys(value, registry);
                        return;

                    case IEnumerable:
                        RefreshCollectionPositions(value, registry);
                        return;
                }

                return;
        }
    }

    private void RefreshCollectionPositions(object? value, ISubjectRegistry registry)
    {
        var collectionPositions = BuildCollectionPositions(value, _children.Count);
        if (collectionPositions is null)
            return;

        try
        {
            lock (_children)
            {

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

                // child is a readonly record struct snapshot from before the update above,
                // so child.Index still holds the old value, which is correct for the oldIndex parameter.
                registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, child.Index, boxedNewIndex);

                // The tracked parents are a second copy of the same index, read by GetParents and by the
                // JSON path helpers, so they move together or the two disagree.
                child.Subject.UpdateParentIndex(Reference, boxedNewIndex);
            }

                // Sort children to match live collection order. Indices that are not positions sort last
                // instead of throwing: a child stranded by an unsupported in-place mutation can still carry
                // a key from an earlier value.
                _children.Sort(static (a, b) => (a.Index as int? ?? int.MaxValue).CompareTo(b.Index as int? ?? int.MaxValue));
                _childrenCache = default;
            }
        }
        finally
        {
            // Cleared so subjects can be GC'd, then handed back for the next refresh on this thread.
            collectionPositions.Clear();
            _reusableCollectionPositions = collectionPositions;
        }
    }

    /// <summary>
    /// Syncs children's keys and parent entries with the live dictionary. Unlike the collection path there is
    /// no order to restore, so children are left in place. Same locking contract as
    /// <see cref="RefreshChildIndices"/>.
    /// </summary>
    private void RefreshDictionaryKeys(object? dictionaryValue, ISubjectRegistry registry)
    {
        if (dictionaryValue is null)
            return;

        // Built outside the lock: it enumerates a caller-supplied dictionary, so holding _children here
        // would run arbitrary enumeration code under it.
        var dictionaryKeys = BuildDictionaryKeys(dictionaryValue);
        try
        {
            lock (_children)
            {
                for (var i = 0; i < _children.Count; i++)
                {
                    var child = _children[i];
                    if (!dictionaryKeys.TryGetValue(child.Subject, out var newKey) || Equals(child.Index, newKey))
                        continue;

                    _children[i] = child with { Index = newKey };
                    _childrenCache = default;

                    // child still holds the old key here, which is what UpdateParentIndex expects.
                    registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, child.Index, newKey);
                    child.Subject.UpdateParentIndex(Reference, newKey);
                }
            }
        }
        finally
        {
            // Cleared so subjects can be GC'd, then handed back for the next refresh on this thread.
            dictionaryKeys.Clear();
            _reusableDictionaryKeys = dictionaryKeys;
        }
    }

    /// <summary>
    /// Maps each subject in the dictionary to its current key, deriving keys as
    /// <c>LifecycleInterceptor.FindSubjectsInProperty</c> does for a dictionary value, including null keys.
    /// The last key wins, which differs from attach, where the first occurrence is the one recorded: it
    /// matches the reverse detach loop, so the tracked parent entry is found by its exact key.
    /// </summary>
    private static Dictionary<IInterceptorSubject, object?> BuildDictionaryKeys(object value)
    {
        // Detached while in use, so a refresh re-entered from the enumeration below builds its own map
        // instead of clearing this one.
        var dictionaryKeys = _reusableDictionaryKeys ?? new Dictionary<IInterceptorSubject, object?>();
        _reusableDictionaryKeys = null;
        dictionaryKeys.Clear();

        switch (value)
        {
            case IInterceptorSubject valueSubject:
                dictionaryKeys[valueSubject] = null;
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subject)
                    {
                        dictionaryKeys[subject] = entry.Key;
                    }
                }

                break;

            case IEnumerable enumerable:
                // Read-only dictionaries that do not implement IDictionary enumerate key-value pairs.
                foreach (var item in enumerable)
                {
                    if (item is not null && SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject))
                    {
                        dictionaryKeys[subject] = key;
                    }
                }

                break;
        }

        return dictionaryKeys;
    }

    /// <summary>
    /// Maps each subject in the collection to its current position.
    /// Uses IList indexed access when available; falls back to ICollection foreach,
    /// then IEnumerable for read-only types that implement neither.
    /// Reuses a ThreadStatic dictionary to avoid allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<IInterceptorSubject, int>? BuildCollectionPositions(object? value, int capacityHint)
    {
        if (value is null)
            return null;

        // Detached while in use, so a refresh re-entered from the enumeration below builds its own map
        // instead of clearing this one.
        var collectionPositions = _reusableCollectionPositions;
        _reusableCollectionPositions = null;
        collectionPositions?.Clear();

        if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint);
                    collectionPositions[subject] = index;
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
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint);
                    collectionPositions[subject] = index;
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
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint);
                    collectionPositions[subject] = index;
                }
                index++;
            }
        }

        return collectionPositions;
    }
}

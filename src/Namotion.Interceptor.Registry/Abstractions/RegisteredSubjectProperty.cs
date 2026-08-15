using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    private const byte ContainerKindUnresolved = 0;
    private const byte ContainerKindNone = 1;
    private const byte ContainerKindReference = 2;
    private const byte ContainerKindCollection = 3;
    private const byte ContainerKindDictionary = 4;

    /// <summary>How much scanning is worth doing, as a multiple of the child count, before rebuilding instead.</summary>
    internal const int RebuildWorkBudgetFactor = 4;

    /// <summary>Below this many children the scan wins whatever the shape, so the rebuild never runs.</summary>
    internal const int RebuildMinimumChildren = 32;

    /// <summary>A pathological write must not pin an oversized buffer on a pool thread for its lifetime.</summary>
    private const int MaximumPooledCapacity = 4096;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusablePositions;

    [ThreadStatic]
    private static List<SubjectPropertyChild>? _reusableRebuild;

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
    /// Syncs the children with the child subjects the property now holds: each child's index is updated
    /// and the children are put in the given order, so that <see cref="Children"/> follows the live
    /// collection or dictionary, dictionaries included. Children the property no longer holds keep their
    /// relative order at the end, where an unsupported in-place mutation can strand them.
    /// Must be called while LifecycleInterceptor's _attachedSubjects lock is held,
    /// because this method acquires _children then _knownSubjects, which is the inverse of
    /// HandleLifecycleChange's lock order. The outer _attachedSubjects lock serializes
    /// both paths and prevents deadlock.
    /// </summary>
    /// <param name="children">The child subjects the property holds, with their indices, in the order the property holds them. Valid for the duration of the call only.</param>
    /// <param name="registry">The subject registry (passed from caller to avoid repeated service resolution per child).</param>
    internal void RefreshChildIndices(ReadOnlySpan<SubjectChildReference> children, ISubjectRegistry registry)
    {
        RefreshChildIndices(children, registry, RebuildMinimumChildren, RebuildWorkBudgetFactor);
    }

    /// <param name="children">The child subjects the property holds, with their indices, in the order the property holds them.</param>
    /// <param name="registry">The subject registry.</param>
    /// <param name="minimumChildrenForRebuild">Overridden by tests, so both placement paths can be driven without building large containers.</param>
    /// <param name="workBudgetFactor">Overridden by tests, so either path can be forced for the same input.</param>
    /// <returns>True when the rebuild path was taken, which is what tests assert the budget on.</returns>
    internal bool RefreshChildIndices(ReadOnlySpan<SubjectChildReference> children, ISubjectRegistry registry,
        int minimumChildrenForRebuild, int workBudgetFactor)
    {
        lock (_children)
        {
            // Scanning is the fastest placement while children sit at or near their slot, and quadratic when
            // they do not. Both of its quadratic terms are charged against one budget: walking past entries to
            // find a child, and the block a displacement has to move. Once the charge exceeds what a rebuild
            // would cost, the remainder is handed over, which bounds the whole method at O(n).
            var budget = (long)workBudgetFactor * _children.Count;
            var work = 0L;
            var slot = 0;

            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index];

                var position = -1;
                for (var i = slot; i < _children.Count; i++)
                {
                    if (ReferenceEquals(_children[i].Subject, child.Subject))
                    {
                        position = i;
                        break;
                    }
                }

                // Charged whether or not the scan found anything: a miss walks the whole remainder, and a
                // subject held at several indices misses on every repeat after the first.
                work += position < 0 ? _children.Count - slot : position - slot + 1;

                // Either a repeat of a subject already placed, so the first index wins as it does on attach,
                // or a subject which an unsupported in-place mutation hid from the lifecycle interceptor.
                if (position < 0)
                {
                    continue;
                }

                // RemoveAt and Insert each shift everything after them, so a displacement costs the rest of
                // the list however near the child happened to be.
                if (position != slot)
                {
                    work += _children.Count - slot;
                }

                if (work > budget && _children.Count - slot >= minimumChildrenForRebuild)
                {
                    // This child is handed over too, so the handover neither drops nor doubles it.
                    RebuildChildren(children[index..], slot, registry);
                    return true;
                }

                var existing = _children[position];
                if (!Equals(existing.Index, child.Index))
                {
                    registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, existing.Index, child.Index);
                    existing = existing with { Index = child.Index };
                    _childrenCache = default;
                }

                if (position == slot)
                {
                    _children[slot] = existing;
                }
                else
                {
                    _children.RemoveAt(position);
                    _children.Insert(slot, existing);
                    _childrenCache = default;
                }

                slot++;
            }

            return false;
        }
    }

    /// <summary>
    /// Places the children from <paramref name="from"/> onwards in one linear pass, for writes that move
    /// enough of them that scanning would be quadratic. Produces exactly what the scan produces.
    /// Caller must hold the <see cref="_children"/> lock.
    /// </summary>
    private void RebuildChildren(ReadOnlySpan<SubjectChildReference> children, int from, ISubjectRegistry registry)
    {
        // Detached while in use: Equals on an index key is caller code, it can write another property, and
        // WriteProperty is re-entrant for a different property, so a nested refresh has to build its own
        // buffers instead of clearing the ones being filled here.
        var positions = _reusablePositions ?? new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        var rebuilt = _reusableRebuild ?? [];

        _reusablePositions = null;
        _reusableRebuild = null;

        try
        {
            for (var i = from; i < _children.Count; i++)
            {
                // TryAdd rather than the indexer: the scan takes the first position holding the subject.
                positions.TryAdd(_children[i].Subject, i);
            }

            foreach (var child in children)
            {
                // Removing both tests membership and marks the subject placed, so a subject held at several
                // indices keeps the first, as attach records it.
                if (!positions.Remove(child.Subject, out var position))
                {
                    continue;
                }

                var existing = _children[position];
                if (!Equals(existing.Index, child.Index))
                {
                    registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, existing.Index, child.Index);
                    existing = existing with { Index = child.Index };
                }

                rebuilt.Add(existing);
            }

            // Children the new value no longer holds keep their relative order at the end. Read off the
            // children rather than the map, whose enumeration order is not specified.
            if (positions.Count > 0)
            {
                for (var i = from; i < _children.Count; i++)
                {
                    if (positions.ContainsKey(_children[i].Subject))
                    {
                        rebuilt.Add(_children[i]);
                    }
                }
            }

            // Spliced only once everything above succeeded, so a throwing comparer cannot leave the children
            // half rebuilt.
            _children.RemoveRange(from, _children.Count - from);
            _children.AddRange(rebuilt);
            _childrenCache = default;
        }
        finally
        {
            var reusable = rebuilt.Capacity <= MaximumPooledCapacity;

            positions.Clear();
            rebuilt.Clear();

            if (reusable)
            {
                _reusablePositions = positions;
                _reusableRebuild = rebuilt;
            }
        }
    }
}

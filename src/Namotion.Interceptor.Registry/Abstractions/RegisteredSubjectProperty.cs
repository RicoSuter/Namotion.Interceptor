using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableChildPositions;

    [ThreadStatic]
    private static List<SubjectPropertyChild>? _reusableChildren;

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    // Entries beyond the first of each subject. While zero, no reported subject can have more than one entry. Written
    // under the _children lock; HasRepeatedChildren reads it without.
    private int _repeatedChildCount;

    private readonly PropertyAttributeAttribute? _attributeMetadata;

    // 0: not computed yet, 1: no public setter, 2: public setter. Computed on first read because it reflects over the setter.
    private byte _hasPublicSetter;

    // 0: not computed yet, 1: no, 2: yes. Cached because segment lookups ask it for every matching property.
    private byte _isInlinePathsProperty;

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
    /// Gets whether this is the [InlinePaths] property of its subject's type.
    /// </summary>
    internal bool IsInlinePathsProperty
    {
        get
        {
            var isInlinePathsProperty = _isInlinePathsProperty;
            if (isInlinePathsProperty == 0)
            {
                isInlinePathsProperty = InlinePathsAttribute.IsInlinePathsProperty(Subject.GetType(), Name) ? (byte)2 : (byte)1;
                _isInlinePathsProperty = isInlinePathsProperty;
            }

            return isInlinePathsProperty == 2;
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
    /// Gets the collection or dictionary items of the property, one entry per key or position that holds a subject.
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
    /// Gets whether the property holds a subject at more than one key or position, so that <see cref="Children"/> has
    /// more than one entry for it.
    /// </summary>
    /// <remarks>
    /// A hint read separately from <see cref="Children"/>: after a concurrent write, a later read of
    /// <see cref="Children"/> may disagree with it.
    /// </remarks>
    public bool HasRepeatedChildren => Volatile.Read(ref _repeatedChildCount) > 0;

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
            _repeatedChildCount = 0;
            _childrenCache = default;
        }
    }

    // Adds an entry per place, the index first. No Contains check needed: the lifecycle attaches a subject to a property
    // once, with all of its places.
    internal void AddChild(IInterceptorSubject subject, object? index, ImmutableArray<object?> additionalIndices)
    {
        lock (_children)
        {
            _children.Insert(GetInsertPosition(index), new SubjectPropertyChild { Subject = subject, Index = index });
            foreach (var additionalIndex in additionalIndices)
            {
                _children.Insert(GetInsertPosition(additionalIndex), new SubjectPropertyChild { Subject = subject, Index = additionalIndex });
            }

            _repeatedChildCount += additionalIndices.Length;
            _childrenCache = default;
        }
    }

    // Removes the last count entries of the subject, which are all of them: the caller takes the count from the
    // parent entries it removed. Both stores are written only by SubjectRegistry, in the same handler call under its
    // _knownSubjects lock, so the subject has as many child entries here as parent entries for this property.
    internal void RemoveChild(IInterceptorSubject subject, int count)
    {
        lock (_children)
        {
            // Searches backwards, as IndexOfChild does.
            var removed = 0;
            for (var i = _children.Count - 1; i >= 0 && removed < count; i--)
            {
                if (_children[i].Subject == subject)
                {
                    _children.RemoveAt(i);
                    removed++;
                }
            }

            Debug.Assert(removed == count, "The child entries of the subject must match its parent entries for the property.");

            if (removed > 0)
            {
                _repeatedChildCount -= removed - 1;
                _childrenCache = default;
            }
        }
    }

    /// <summary>
    /// Gives reported children and their parent entries the keys or positions they have in the value now.
    /// Must be called inside the registry's _knownSubjects lock, which every change to children and
    /// parent entries takes first.
    /// </summary>
    /// <param name="children">The reported children with every key or position they have now, each subject's as one consecutive run.</param>
    /// <param name="knownSubjects">The registered subjects, to find the parent entries of the children.</param>
    internal void UpdateChildIndices(ReadOnlySpan<SubjectChild> children, Dictionary<IInterceptorSubject, RegisteredSubject> knownSubjects)
    {
        lock (_children)
        {
            // The update in place moves one entry per reported subject, so it serves any change that reports no subject
            // with more than one place or entry, whatever other subjects repeat.
            if (HasRepeatedSubject(children) || (_repeatedChildCount > 0 && HasChildWithSeveralEntries(children)))
            {
                ReplaceChildPlaces(children, knownSubjects);
                return;
            }

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

    private static bool HasRepeatedSubject(ReadOnlySpan<SubjectChild> children)
    {
        foreach (var group in children.GroupBySubject())
        {
            if (group.Children.Length > 1)
                return true;
        }

        return false;
    }

    // Whether a reported subject has more than one entry. Must be called under the _children lock.
    private bool HasChildWithSeveralEntries(ReadOnlySpan<SubjectChild> children)
    {
        if (children.Length == 1)
        {
            var subject = children[0].Subject;
            var count = 0;
            foreach (var child in _children)
            {
                if (child.Subject == subject && ++count > 1)
                    return true;
            }

            return false;
        }

        var counts = _reusableChildPositions ??= new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        foreach (var child in children)
        {
            counts[child.Subject] = 0;
        }

        var hasSeveral = false;
        foreach (var child in _children)
        {
            ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(counts, child.Subject);
            if (!Unsafe.IsNullRef(ref count) && ++count > 1)
            {
                hasSeveral = true;
                break;
            }
        }

        // Release references so subjects can be GC'd on idle threads, and leave the map empty for the update. Nothing
        // above runs user code or throws.
        counts.Clear();
        return hasSeveral;
    }

    // Replaces the entries of each reported subject with its run of places. Must be called under the _children lock.
    private void ReplaceChildPlaces(ReadOnlySpan<SubjectChild> children, Dictionary<IInterceptorSubject, RegisteredSubject> knownSubjects)
    {
        // The first place of each reported subject, where its run starts.
        var starts = _reusableChildPositions ??= new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        var groupStart = 0;
        foreach (var group in children.GroupBySubject())
        {
            starts.TryAdd(group.Subject, groupStart);
            groupStart += group.Children.Length;
        }

        // A run takes the place of its subject's first entry, which keeps dictionary children in attach order;
        // collection children are sorted by position afterwards.
        var replaced = _reusableChildren ??= [];
        foreach (var child in _children)
        {
            ref var start = ref CollectionsMarshal.GetValueRefOrNullRef(starts, child.Subject);
            if (Unsafe.IsNullRef(ref start))
            {
                replaced.Add(child);
                continue;
            }

            // A later entry of a subject whose run is already in.
            if (start < 0)
                continue;

            var end = start + 1;
            while (end < children.Length && ReferenceEquals(children[end].Subject, child.Subject))
            {
                end++;
            }

            var places = children[start..end];
            foreach (var place in places)
            {
                replaced.Add(new SubjectPropertyChild { Subject = place.Subject, Index = place.Index });
            }

            knownSubjects.GetValueOrDefault(child.Subject)?.ReplaceParentIndices(this, places);
            start = -1;
        }

        _repeatedChildCount += replaced.Count - _children.Count;
        _children.Clear();
        _children.AddRange(replaced);
        SortByPosition();
        _childrenCache = default;

        // Release references so subjects can be GC'd on idle threads. Nothing above runs user code or throws.
        replaced.Clear();
        starts.Clear();
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

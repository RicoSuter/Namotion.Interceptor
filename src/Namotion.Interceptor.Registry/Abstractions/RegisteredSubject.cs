using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubject
{
    private readonly Lock _lock = new();

    private volatile FrozenDictionary<string, RegisteredSubjectProperty> _properties;

    // Most subjects have exactly one parent, so the first is stored inline and the
    // overflow list is allocated only on the second. Empty sentinel:
    // _firstParent.Property is null (a real entry never has a null Property).
    private SubjectPropertyParent _firstParent;
    private List<SubjectPropertyParent>? _additionalParents;

    // Raw array instead of ImmutableArray because the struct can't be read atomically;
    // the Volatile.Write publish (under _lock) pairs with the lock-free Volatile.Read
    // in the getter so readers see fully built contents.
    private SubjectPropertyParent[]? _parentsSnapshot;

    [JsonIgnore] public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Gets the current reference count (number of parent references).
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public int ReferenceCount => Subject.GetReferenceCount();

    /// <summary>
    /// Gets the properties which reference this subject.
    /// One entry per key or position that holds the subject, grouped by property in the order of its value (<c>null</c> for a subject reference).
    /// Thread-safe; reads are lock-free once the snapshot is cached.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> Parents
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var snapshot = Volatile.Read(ref _parentsSnapshot);
            return snapshot is not null
                ? ImmutableCollectionsMarshal.AsImmutableArray(snapshot)
                : GetParentsSlow();
        }
    }

    private ImmutableArray<SubjectPropertyParent> GetParentsSlow()
    {
        lock (_lock)
        {
            var snapshot = _parentsSnapshot;
            if (snapshot is null)
            {
                snapshot = BuildParentsSnapshot();
                Volatile.Write(ref _parentsSnapshot, snapshot);
            }
            return ImmutableCollectionsMarshal.AsImmutableArray(snapshot);
        }
    }

    // Must be called under _lock. Published snapshots are never mutated in place because
    // lock-free readers rely on it: mutators invalidate and the next reader rebuilds.
    private SubjectPropertyParent[] BuildParentsSnapshot()
    {
        if (_firstParent.Property is null)
            return [];

        if (_additionalParents is null || _additionalParents.Count == 0)
            return [_firstParent];

        var array = new SubjectPropertyParent[1 + _additionalParents.Count];
        array[0] = _firstParent;
        _additionalParents.CopyTo(array, 1);
        return array;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvalidateParentsSnapshot() => Volatile.Write(ref _parentsSnapshot, null);

    /// <summary>
    /// Gets all registered properties.
    /// </summary>
    public ImmutableArray<RegisteredSubjectProperty> Properties => _properties.Values;

    /// <summary>
    /// Gets all attributes that are attached to this property.
    /// </summary>
    public IEnumerable<RegisteredSubjectProperty> GetPropertyAttributes(string propertyName)
    {
        foreach (var property in _properties.Values)
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
        foreach (var property in _properties.Values)
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
        lock (_lock)
        {
            var entry = new SubjectPropertyParent { Property = parent, Index = index };
            if (_firstParent.Property is null)
            {
                _firstParent = entry;
            }
            else
            {
                _additionalParents ??= [];
                _additionalParents.Add(entry);
            }
            InvalidateParentsSnapshot();
        }
    }

    // Removes every entry of the property, one per key or position that held the subject, and returns how many.
    internal int RemoveParent(RegisteredSubjectProperty property)
    {
        lock (_lock)
        {
            var removed = RemoveAdditionalParents(property, 0);
            if (_firstParent.Property == property)
            {
                PromoteFirstParentFromAdditional();
                return removed + 1;
            }

            if (removed > 0)
            {
                InvalidateParentsSnapshot();
            }

            return removed;
        }
    }

    internal void UpdateParentIndex(RegisteredSubjectProperty property, object? newIndex)
    {
        lock (_lock)
        {
            var entry = new SubjectPropertyParent { Property = property, Index = newIndex };
            if (_firstParent.Property == property)
            {
                _firstParent = entry;
                InvalidateParentsSnapshot();
                return;
            }

            var position = IndexOfAdditionalParent(property);
            if (position >= 0)
            {
                _additionalParents![position] = entry;
                InvalidateParentsSnapshot();
            }
        }
    }

    // Replaces the entries of the property with one per place (at least one), at the position of its first entry.
    internal void ReplaceParentIndices(RegisteredSubjectProperty property, ReadOnlySpan<SubjectChild> places)
    {
        lock (_lock)
        {
            // The first place overwrites the first entry, so one entry and one place need no list.
            var entry = new SubjectPropertyParent { Property = property, Index = places[0].Index };
            int next;
            if (_firstParent.Property == property)
            {
                _firstParent = entry;
                next = 0;
            }
            else
            {
                var position = IndexOfAdditionalParent(property);
                if (position < 0)
                    return;

                _additionalParents![position] = entry;
                next = position + 1;
            }

            RemoveAdditionalParents(property, next);
            InsertAdditionalParents(next, property, places[1..]);
            InvalidateParentsSnapshot();
        }
    }

    // Removes the entries of the property from the overflow list, starting at the given position, keeping the order
    // of the others, and returns how many. Caller must hold _lock.
    private int RemoveAdditionalParents(RegisteredSubjectProperty property, int start)
    {
        var parents = _additionalParents;
        if (parents is null)
            return 0;

        var span = CollectionsMarshal.AsSpan(parents);
        var kept = start;
        for (var i = start; i < span.Length; i++)
        {
            if (span[i].Property != property)
                span[kept++] = span[i];
        }

        var removed = span.Length - kept;
        parents.RemoveRange(kept, removed);
        return removed;
    }

    // Caller must hold _lock.
    private void InsertAdditionalParents(int position, RegisteredSubjectProperty property, ReadOnlySpan<SubjectChild> places)
    {
        if (places.IsEmpty)
            return;

        var parents = _additionalParents ??= [];
        var count = parents.Count;
        CollectionsMarshal.SetCount(parents, count + places.Length);
        var span = CollectionsMarshal.AsSpan(parents);
        span[position..count].CopyTo(span[(position + places.Length)..]);
        for (var i = 0; i < places.Length; i++)
        {
            span[position + i] = new SubjectPropertyParent { Property = property, Index = places[i].Index };
        }
    }

    // Caller must hold _lock.
    private int IndexOfAdditionalParent(RegisteredSubjectProperty property)
    {
        if (_additionalParents is not null)
        {
            for (var i = 0; i < _additionalParents.Count; i++)
            {
                if (_additionalParents[i].Property == property)
                    return i;
            }
        }

        return -1;
    }

    // Promotes the head (not the tail) of the overflow list so the surviving parents
    // keep their insertion order. Caller must hold _lock.
    private void PromoteFirstParentFromAdditional()
    {
        if (_additionalParents is not null && _additionalParents.Count > 0)
        {
            _firstParent = _additionalParents[0];
            _additionalParents.RemoveAt(0);
        }
        else
        {
            _firstParent = default;
        }
        InvalidateParentsSnapshot();
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
            setValue is not null ? (s, v) => ((IInterceptorExecutor)s.Context).SetPropertyValue(name, v, getValue?.Invoke(s), setValue) : null,
            isIntercepted: true,
            isDynamic: true));

        var property = AddPropertyInternal(name, type, attributes);

        // Fires a null→value transition for lifecycle tracking of subject-valued initial values.
        if (property.Reference.Metadata.IsDerived)
        {
            // TODO(perf): AttachProperty has already evaluated the getter and seeded LastKnownValue, yet
            // publishing reads it again, and once more for the change notification when one is consumed.
            // For derived-with-setter the write also re-enters RecalculateDerivedProperty, another
            // evaluation. Consider a dedicated lifecycle notification for derived properties.
            property.Reference.PublishInitialDerivedPropertyValue();
        }
        else
        {
            property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject), null, delegate { });
        }

        return property;
    }

    private RegisteredSubjectProperty AddPropertyInternal(string name, Type type, Attribute[] attributes)
    {
        var subjectProperty = new RegisteredSubjectProperty(this, name, type, attributes);

        lock (_lock)
        {
            var newProperties = _properties
                .Append(KeyValuePair.Create(subjectProperty.Name, subjectProperty))
                .ToFrozenDictionary(p => p.Key, p => p.Value);

            _properties = newProperties;

            foreach (var property in newProperties.Values)
            {
                property.AttributesCache = null;
            }
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);
        return subjectProperty;
    }
}

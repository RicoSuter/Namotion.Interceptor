using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Attributes;
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

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
        {
            var entry = new SubjectPropertyParent { Property = parent, Index = index };
            if (_firstParent.Property is not null && _firstParent.Equals(entry))
            {
                PromoteFirstParentFromAdditional();
                return;
            }

            if (_additionalParents is not null)
            {
                var indexInList = _additionalParents.IndexOf(entry);
                if (indexInList >= 0)
                {
                    _additionalParents.RemoveAt(indexInList);
                    InvalidateParentsSnapshot();
                }
            }
        }
    }

    internal void RemoveParentsByProperty(RegisteredSubjectProperty parent)
    {
        lock (_lock)
        {
            var changed = false;

            if (_additionalParents is not null && _additionalParents.Count > 0)
            {
                for (var i = _additionalParents.Count - 1; i >= 0; i--)
                {
                    if (_additionalParents[i].Property == parent)
                    {
                        _additionalParents.RemoveAt(i);
                        changed = true;
                    }
                }
            }

            if (_firstParent.Property == parent)
            {
                PromoteFirstParentFromAdditional();
                changed = true;
            }

            if (changed)
            {
                InvalidateParentsSnapshot();
            }
        }
    }

    internal void UpdateParentIndex(RegisteredSubjectProperty property, object? oldIndex, object? newIndex)
    {
        lock (_lock)
        {
            var oldEntry = new SubjectPropertyParent { Property = property, Index = oldIndex };
            if (_firstParent.Property is not null && _firstParent.Equals(oldEntry))
            {
                _firstParent = new SubjectPropertyParent { Property = property, Index = newIndex };
                InvalidateParentsSnapshot();
                return;
            }

            if (_additionalParents is not null)
            {
                var indexInList = _additionalParents.IndexOf(oldEntry);
                if (indexInList >= 0)
                {
                    _additionalParents[indexInList] = new SubjectPropertyParent { Property = property, Index = newIndex };
                    InvalidateParentsSnapshot();
                }
            }
        }
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
    /// <exception cref="InvalidOperationException">The subject already has a property with this name, or an attribute
    /// with the same property and attribute name. Neither the subject nor the registry changes in that case.</exception>
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
    /// <exception cref="InvalidOperationException">The subject already has a property with this name, or an attribute
    /// with the same property and attribute name. Neither the subject nor the registry changes in that case.</exception>
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
    /// <exception cref="InvalidOperationException">The subject already has a property with this name, or an attribute
    /// with the same property and attribute name. Neither the subject nor the registry changes in that case.</exception>
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
    /// <exception cref="InvalidOperationException">The subject already has a property with this name, or
    /// <paramref name="attributes"/> carries a <see cref="PropertyAttributeAttribute"/> whose property already
    /// has an attribute with that attribute name. Neither the subject nor the registry changes in that case.</exception>
    public RegisteredSubjectProperty AddProperty(
        string name,
        Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue,
        params Attribute[] attributes)
    {
        return AddPropertyCore(name, type, getValue, setValue, attributes, returnExistingAttribute: false);
    }

    /// <summary>
    /// Adds the property, or, with <paramref name="returnExistingAttribute"/>, returns the attribute that
    /// already provides the same property and attribute name pair instead of throwing.
    /// </summary>
    internal RegisteredSubjectProperty AddPropertyCore(
        string name,
        Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue,
        Attribute[] attributes,
        bool returnExistingAttribute)
    {
        var subjectProperty = new RegisteredSubjectProperty(this, name, type, attributes);
        var metadata = new SubjectPropertyMetadata(
            name,
            type,
            attributes,
            getValue is not null ? s => ((IInterceptorExecutor)s.Context).GetPropertyValue(name, getValue) : null,
            setValue is not null ? (s, v) => ((IInterceptorExecutor)s.Context).SetPropertyValue(name, v, getValue?.Invoke(s), setValue) : null,
            isIntercepted: true,
            isDynamic: true);

        // Lock order: the subject's SyncRoot first, then _lock. Getters and setters run under SyncRoot
        // and may take _lock (Parents does), so _lock must never be held while SyncRoot is acquired.
        lock (Subject.SyncRoot)
        lock (_lock)
        {
            // The pair check runs before the name check so that a get-or-add finds an attribute
            // registered under this very name instead of failing on the name.
            if (subjectProperty.IsAttribute)
            {
                var attributeMetadata = subjectProperty.AttributeMetadata;
                var existingAttribute = TryGetPropertyAttribute(attributeMetadata.PropertyName, attributeMetadata.AttributeName);
                if (existingAttribute is not null)
                {
                    if (returnExistingAttribute)
                    {
                        return existingAttribute;
                    }

                    throw new InvalidOperationException(
                        $"The property '{attributeMetadata.PropertyName}' of subject '{Subject.GetType().Name}' " +
                        $"already has an attribute named '{attributeMetadata.AttributeName}' (property '{existingAttribute.Name}').");
                }
            }

            if (_properties.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"The subject '{Subject.GetType().Name}' already has a property named '{name}'.");
            }

            Subject.AddProperties(metadata);

            var newProperties = _properties
                .Append(KeyValuePair.Create(name, subjectProperty))
                .ToFrozenDictionary(p => p.Key, p => p.Value);

            _properties = newProperties;

            foreach (var property in newProperties.Values)
            {
                property.AttributesCache = null;
            }
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);

        // Fires a null→value transition for lifecycle tracking of subject-valued initial values.
        // TODO(perf): For derived-with-setter this re-enters RecalculateDerivedProperty (total
        // 3 getter invocations: AttachProperty + invoke below + recalc), but AttachProperty has
        // already seeded LastKnownValue. Consider a dedicated lifecycle notification for derived,
        // or passing currentValue so PropertyValueEqualityCheckHandler short-circuits the write.
        subjectProperty.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null,
            null, delegate { });

        return subjectProperty;
    }
}

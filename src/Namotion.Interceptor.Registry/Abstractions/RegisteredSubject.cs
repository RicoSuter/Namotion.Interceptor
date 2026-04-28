using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubject
{
    private readonly Lock _lock = new();

    private volatile FrozenDictionary<string, RegisteredSubjectProperty> _properties;

    // Inline single-parent storage. Most subjects have exactly one parent; the
    // overflow list is allocated only on the second parent. Empty sentinel:
    // _firstParent.Property is null (Property is a class, never null on a real entry).
    private SubjectPropertyParent _firstParent;
    private List<SubjectPropertyParent>? _additionalParents;

    // Lazily built snapshot of Parents. default (IsDefault) == not cached;
    // invalidated on every mutation so reads return a fresh view but repeated
    // reads without intervening mutations are allocation-free.
    private ImmutableArray<SubjectPropertyParent> _parentsSnapshot;

    [JsonIgnore] public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Gets the current reference count (number of parent references).
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public int ReferenceCount => Subject.GetReferenceCount();

    /// <summary>
    /// Gets the properties which reference this subject.
    /// Thread-safe: returns a cached immutable snapshot, rebuilt on demand
    /// after a mutation invalidates it.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> Parents
    {
        get
        {
            lock (_lock)
            {
                if (_parentsSnapshot.IsDefault)
                {
                    _parentsSnapshot = BuildParentsSnapshot();
                }
                return _parentsSnapshot;
            }
        }
    }

    private ImmutableArray<SubjectPropertyParent> BuildParentsSnapshot()
    {
        if (_firstParent.Property is null)
        {
            return ImmutableArray<SubjectPropertyParent>.Empty;
        }

        if (_additionalParents is null || _additionalParents.Count == 0)
        {
            return ImmutableArray.Create(_firstParent);
        }

        var builder = ImmutableArray.CreateBuilder<SubjectPropertyParent>(1 + _additionalParents.Count);
        builder.Add(_firstParent);
        for (var i = 0; i < _additionalParents.Count; i++)
        {
            builder.Add(_additionalParents[i]);
        }
        return builder.MoveToImmutable();
    }

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
                _additionalParents ??= new List<SubjectPropertyParent>();
                _additionalParents.Add(entry);
            }
            _parentsSnapshot = default;
        }
    }

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
        {
            var entry = new SubjectPropertyParent { Property = parent, Index = index };
            if (_firstParent.Property is not null && _firstParent.Equals(entry))
            {
                PromoteLastFromAdditional();
                return;
            }

            if (_additionalParents is not null)
            {
                var indexInList = _additionalParents.IndexOf(entry);
                if (indexInList >= 0)
                {
                    _additionalParents.RemoveAt(indexInList);
                    _parentsSnapshot = default;
                }
            }
        }
    }

    // Called only during context-detach cleanup before the subject is dropped from
    // the registry, so the order of any remaining (non-matching) entries is not
    // observable to callers — we use that freedom to promote-from-tail when the
    // inline slot matches.
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
                PromoteLastFromAdditional();
                changed = true;
            }

            if (changed)
            {
                _parentsSnapshot = default;
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
                _parentsSnapshot = default;
                return;
            }

            if (_additionalParents is not null)
            {
                var indexInList = _additionalParents.IndexOf(oldEntry);
                if (indexInList >= 0)
                {
                    _additionalParents[indexInList] = new SubjectPropertyParent { Property = property, Index = newIndex };
                    _parentsSnapshot = default;
                }
            }
        }
    }

    // Clears the inline first-parent slot, O(1) tail-pop promoting from overflow if any,
    // and invalidates the snapshot. Caller must hold _lock.
    private void PromoteLastFromAdditional()
    {
        if (_additionalParents is not null && _additionalParents.Count > 0)
        {
            var lastIndex = _additionalParents.Count - 1;
            _firstParent = _additionalParents[lastIndex];
            _additionalParents.RemoveAt(lastIndex);
        }
        else
        {
            _firstParent = default;
        }
        _parentsSnapshot = default;
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
        // TODO(perf): For derived-with-setter this re-enters RecalculateDerivedProperty (total
        // 3 getter invocations: AttachProperty + invoke below + recalc), but AttachProperty has
        // already seeded LastKnownValue. Consider a dedicated lifecycle notification for derived,
        // or passing currentValue so PropertyValueEqualityCheckHandler short-circuits the write.
        property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null,
            null, delegate { });

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

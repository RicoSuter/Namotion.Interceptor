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

    // Most subjects have exactly one parent property, so the first relationship group is stored inline
    // and overflow storage is allocated only for additional parent properties.
    private ParentRelationshipGroup? _firstParentGroup;
    private List<ParentRelationshipGroup>? _additionalParentGroups;

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
        if (_firstParentGroup is null)
            return [];

        var count = _firstParentGroup.Relationships.Length;
        if (_additionalParentGroups is not null)
        {
            foreach (var group in _additionalParentGroups)
            {
                count += group.Relationships.Length;
            }
        }

        var array = new SubjectPropertyParent[count];
        var offset = AddParentRelationships(array, 0, _firstParentGroup);
        if (_additionalParentGroups is not null)
        {
            foreach (var group in _additionalParentGroups)
            {
                offset = AddParentRelationships(array, offset, group);
            }
        }

        return array;
    }

    private static int AddParentRelationships(
        SubjectPropertyParent[] destination,
        int offset,
        ParentRelationshipGroup group)
    {
        foreach (var relationship in group.Relationships)
        {
            destination[offset++] = new SubjectPropertyParent
            {
                Property = group.Property,
                Index = relationship.Index
            };
        }

        return offset;
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

    internal void AddParentRelationship(
        RegisteredSubjectProperty property,
        SubjectPropertyRelationship relationship)
    {
        lock (_lock)
        {
            if (FindParentGroup(property) >= 0)
            {
                return;
            }

            AddParentGroup(new ParentRelationshipGroup(property, [relationship]));
            InvalidateParentsSnapshot();
        }
    }

    internal void ReplaceParentGroup(
        RegisteredSubjectProperty property,
        ImmutableArray<SubjectPropertyRelationship> relationships)
    {
        lock (_lock)
        {
            if (relationships.IsEmpty)
            {
                RemoveParentGroupCore(property);
                return;
            }

            var group = new ParentRelationshipGroup(property, relationships);
            var groupIndex = FindParentGroup(property);
            if (groupIndex == 0)
            {
                _firstParentGroup = group;
            }
            else if (groupIndex > 0)
            {
                _additionalParentGroups![groupIndex - 1] = group;
            }
            else
            {
                AddParentGroup(group);
            }

            InvalidateParentsSnapshot();
        }
    }

    internal void RemoveParentGroup(RegisteredSubjectProperty property)
    {
        lock (_lock)
        {
            RemoveParentGroupCore(property);
        }
    }

    private void RemoveParentGroupCore(RegisteredSubjectProperty property)
    {
        var groupIndex = FindParentGroup(property);
        if (groupIndex < 0)
        {
            return;
        }

        if (groupIndex == 0)
        {
            if (_additionalParentGroups is { Count: > 0 } additionalGroups)
            {
                _firstParentGroup = additionalGroups[0];
                additionalGroups.RemoveAt(0);
                if (additionalGroups.Count == 0)
                {
                    _additionalParentGroups = null;
                }
            }
            else
            {
                _firstParentGroup = null;
            }
        }
        else
        {
            _additionalParentGroups!.RemoveAt(groupIndex - 1);
            if (_additionalParentGroups.Count == 0)
            {
                _additionalParentGroups = null;
            }
        }

        InvalidateParentsSnapshot();
    }

    private int FindParentGroup(RegisteredSubjectProperty property)
    {
        if (_firstParentGroup is null)
        {
            return -1;
        }

        if (ReferenceEquals(_firstParentGroup.Property, property))
        {
            return 0;
        }

        if (_additionalParentGroups is not null)
        {
            for (var index = 0; index < _additionalParentGroups.Count; index++)
            {
                if (ReferenceEquals(_additionalParentGroups[index].Property, property))
                {
                    return index + 1;
                }
            }
        }

        return -1;
    }

    private void AddParentGroup(ParentRelationshipGroup group)
    {
        if (_firstParentGroup is null)
        {
            _firstParentGroup = group;
            return;
        }

        _additionalParentGroups ??= [];
        _additionalParentGroups.Add(group);
    }

    private sealed class ParentRelationshipGroup(
        RegisteredSubjectProperty property,
        ImmutableArray<SubjectPropertyRelationship> relationships)
    {
        public RegisteredSubjectProperty Property { get; } = property;

        public ImmutableArray<SubjectPropertyRelationship> Relationships { get; } = relationships;
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

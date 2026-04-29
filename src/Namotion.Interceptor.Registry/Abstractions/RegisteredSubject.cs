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

    // Primary storage for all registered members (properties and attributes).
    // Writers build a new FrozenDictionary under _lock and assign atomically;
    // readers observe a consistent snapshot via the volatile reference read.
    private volatile FrozenDictionary<string, RegisteredSubjectMember> _members;

    // Filtered view of _members excluding attributes. Rebuilt under _lock on
    // non-attribute adds; the volatile reference guarantees publication of the
    // new array to readers on weak memory models (ARM). The array itself is
    // never mutated after publication, so ImmutableCollectionsMarshal can wrap
    // it as an ImmutableArray without copying.
    private volatile RegisteredSubjectProperty[] _propertiesSnapshot = [];

    private ImmutableArray<SubjectPropertyParent> _parents = [];

    /// <summary>
    /// Gets the subject this registration wraps.
    /// </summary>
    [JsonIgnore] public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Gets the current reference count (number of parent references).
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public int ReferenceCount => Subject.GetReferenceCount();

    /// <summary>
    /// Gets the properties which reference this subject.
    /// Thread-safe: lock ensures atomic struct copy during read.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> Parents
    {
        get
        {
            lock (_lock)
                return _parents;
        }
    }

    /// <summary>
    /// Gets all registered members (properties, including attributes).
    /// </summary>
    /// <remarks>
    /// Each individual read returns a consistent snapshot, but reading <see cref="Members"/>
    /// and <see cref="Properties"/> across concurrent writes may observe them at different
    /// versions. The two views are quiescently consistent — they converge once writes settle.
    /// </remarks>
    public IReadOnlyDictionary<string, RegisteredSubjectMember> Members => _members;

    /// <summary>
    /// Gets all registered properties (attributes are excluded; access via
    /// <see cref="RegisteredSubjectMember.Attributes"/>).
    /// </summary>
    /// <remarks>
    /// Each individual read returns a consistent snapshot, but reading <see cref="Properties"/>
    /// and <see cref="Members"/> across concurrent writes may observe them at different
    /// versions. The two views are quiescently consistent — they converge once writes settle.
    /// </remarks>
    public ImmutableArray<RegisteredSubjectProperty> Properties
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ImmutableCollectionsMarshal.AsImmutableArray(_propertiesSnapshot);
    }

    /// <summary>
    /// Gets a member by name (property or attribute).
    /// </summary>
    /// <param name="memberName">The member name.</param>
    /// <returns>The member or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectMember? TryGetMember(string memberName)
    {
        return _members.GetValueOrDefault(memberName);
    }

    /// <summary>
    /// Gets the property with the given name. An attribute registered under the
    /// supplied name is also returned because attributes inherit from properties;
    /// use <see cref="TryGetMember"/> when the intent is to look up either kind.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The property or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        return _members.GetValueOrDefault(propertyName) as RegisteredSubjectProperty;
    }

    /// <summary>
    /// Initializes a new <see cref="RegisteredSubject"/> and populates its members
    /// from the subject's properties.
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    public RegisteredSubject(IInterceptorSubject subject)
    {
        Subject = subject;

        var members = new Dictionary<string, RegisteredSubjectMember>();
        foreach (var property in subject.Properties)
        {
            members[property.Key] = CreateMember(
                this, property.Key, property.Value.Type, property.Value.Attributes);
        }

        _members = members.ToFrozenDictionary();

        PopulateInitialCaches();
    }

    internal void AddParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
        {
            _parents = _parents.Add(new SubjectPropertyParent { Property = parent, Index = index });
        }
    }

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_lock)
        {
            _parents = _parents.Remove(new SubjectPropertyParent { Property = parent, Index = index });
        }
    }

    internal void RemoveParentsByProperty(RegisteredSubjectProperty parent)
    {
        lock (_lock)
        {
            var parents = _parents;
            for (var i = parents.Length - 1; i >= 0; i--)
            {
                if (parents[i].Property == parent)
                {
                    parents = parents.RemoveAt(i);
                }
            }

            _parents = parents;
        }
    }

    internal void UpdateParentIndex(RegisteredSubjectProperty property, object? oldIndex, object? newIndex)
    {
        lock (_lock)
        {
            var oldParent = new SubjectPropertyParent { Property = property, Index = oldIndex };
            var idx = _parents.IndexOf(oldParent);
            if (idx >= 0)
            {
                _parents = _parents.SetItem(idx, new SubjectPropertyParent { Property = property, Index = newIndex });
            }
        }
    }

    /// <summary>
    /// Adds a dynamic derived property to the subject with tracking of dependencies.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The optional value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes.</param>
    /// <returns>The created property.</returns>
    public RegisteredSubjectProperty AddDerivedProperty<TProperty>(string name,
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddProperty(name, typeof(TProperty),
            getValue is not null ? x => (TProperty)getValue(x)! : null,
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null,
            AppendAttribute(attributes, new DerivedAttribute()));
    }

    /// <summary>
    /// Adds a dynamic property to the subject.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The optional value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes.</param>
    /// <returns>The created property.</returns>
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
    /// <param name="name">The property name.</param>
    /// <param name="type">The property type.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The optional value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes.</param>
    /// <returns>The created property.</returns>
    public RegisteredSubjectProperty AddDerivedProperty(string name,
        Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddProperty(name, type, getValue, setValue,
            AppendAttribute(attributes, new DerivedAttribute()));
    }

    /// <summary>
    /// Adds a dynamic property with backing data to the subject.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="type">The property type.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes.</param>
    /// <returns>The created property.</returns>
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
        var subjectProperty = CreateMember(this, name, type, attributes);

        lock (_lock)
        {
            // Rebuild _members with an explicit dictionary copy; avoids LINQ enumerator
            // wrappers. FrozenDictionary rebuild cost is unchanged (O(N) perfect-hash build).
            var newMembers = new Dictionary<string, RegisteredSubjectMember>(_members.Count + 1);
            foreach (var existing in _members)
                newMembers[existing.Key] = existing.Value;
            newMembers[subjectProperty.Name] = subjectProperty;
            _members = newMembers.ToFrozenDictionary();

            // Publish the new member's own AttributesCache. Scans _members for any
            // attributes whose MemberName targets this member (covers the rare case
            // where an attribute was registered before its owner). A reader that
            // observes the new member via _members before this assignment lands
            // sees the empty-array default from RegisteredSubjectMember, never null.
            subjectProperty.AttributesCache = ComputeAttributesFor(subjectProperty.Name);

            if (subjectProperty is RegisteredSubjectAttribute newAttribute)
            {
                // Rebuild the target member's cache to include the new attribute.
                // Cross-member visibility lag: between the _members publish above and
                // this assignment, a reader can observe the new attribute via
                // Members[name]/TryGetMember while parent.Attributes still returns the
                // pre-add array. Both views are individually consistent (no nulls, no
                // torn reads) and converge once this write lands — quiescent
                // consistency, not strong consistency. Callers that need the two views
                // to agree must serialize against AddAttribute externally.
                if (_members.TryGetValue(newAttribute.MemberName, out var targetMember)
                    && !ReferenceEquals(targetMember, newAttribute))
                {
                    targetMember.AttributesCache = ComputeAttributesFor(newAttribute.MemberName);
                }
            }
            else
            {
                // Regular property: extend the properties snapshot.
                var existing = _propertiesSnapshot;
                var extended = new RegisteredSubjectProperty[existing.Length + 1];
                Array.Copy(existing, extended, existing.Length);
                extended[existing.Length] = subjectProperty;
                _propertiesSnapshot = extended;
            }
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);
        return subjectProperty;
    }

    /// <summary>
    /// Creates a <see cref="RegisteredSubjectAttribute"/> when the reflection set
    /// includes a <see cref="MemberAttributeAttribute"/>, otherwise a plain
    /// <see cref="RegisteredSubjectProperty"/>.
    /// </summary>
    private static RegisteredSubjectProperty CreateMember(
        RegisteredSubject parent, string name, Type type,
        IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        foreach (var reflectionAttribute in reflectionAttributes)
        {
            if (reflectionAttribute is MemberAttributeAttribute memberAttribute)
                return new RegisteredSubjectAttribute(parent, name, type, reflectionAttributes, memberAttribute);
        }

        return new RegisteredSubjectProperty(parent, name, type, reflectionAttributes);
    }

    /// <summary>
    /// Populates each member's <see cref="RegisteredSubjectMember.AttributesCache"/>
    /// and <see cref="_propertiesSnapshot"/> from the initial <see cref="_members"/>.
    /// Called once at construction, before any concurrent reader can observe this subject.
    /// </summary>
    private void PopulateInitialCaches()
    {
        // Fast path: no attributes. Common case for attribute-free subjects.
        // Skips the dictionary + per-member List<T> allocations, and lets the
        // default empty AttributesCache from RegisteredSubjectMember stand.
        var hasAttributes = false;
        foreach (var member in _members.Values)
        {
            if (member is RegisteredSubjectAttribute)
            {
                hasAttributes = true;
                break;
            }
        }

        if (!hasAttributes)
        {
            var snapshot = new RegisteredSubjectProperty[_members.Count];
            var index = 0;
            foreach (var member in _members.Values)
            {
                snapshot[index++] = (RegisteredSubjectProperty)member;
            }
            _propertiesSnapshot = snapshot;
            return;
        }

        // hasAttributes == true here, so attributesByMember will gain at least one entry.
        var attributesByMember = new Dictionary<string, List<RegisteredSubjectAttribute>>();
        var properties = new List<RegisteredSubjectProperty>(_members.Count);

        foreach (var member in _members.Values)
        {
            if (member is RegisteredSubjectAttribute attribute)
            {
                if (!attributesByMember.TryGetValue(attribute.MemberName, out var list))
                {
                    list = [];
                    attributesByMember[attribute.MemberName] = list;
                }
                list.Add(attribute);
            }
            else
            {
                properties.Add((RegisteredSubjectProperty)member);
            }
        }

        _propertiesSnapshot = properties.ToArray();

        foreach (var (memberName, list) in attributesByMember)
        {
            if (_members.TryGetValue(memberName, out var member))
            {
                member.AttributesCache = list.ToArray();
            }
        }
    }

    /// <summary>
    /// Scans <see cref="_members"/> for all attributes targeting <paramref name="memberName"/>.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private RegisteredSubjectAttribute[] ComputeAttributesFor(string memberName)
    {
        // TODO(perf): O(M) scan over all members per call, invoked up to twice per
        // AddPropertyInternal. For N sequential dynamic attribute adds against a single
        // owner this is O(N²). Bounded in practice (attributes per property are typically
        // ≤5), so we accept the cost. If profiling shows this as a hot path, maintain a
        // per-target-name index alongside _members.
        List<RegisteredSubjectAttribute>? result = null;
        foreach (var member in _members.Values)
        {
            if (member is RegisteredSubjectAttribute attribute && attribute.MemberName == memberName)
            {
                result ??= [];
                result.Add(attribute);
            }
        }
        return result is null ? Array.Empty<RegisteredSubjectAttribute>() : result.ToArray();
    }

    /// <summary>
    /// Allocation-minimal single-attribute append. Avoids the LINQ Concat/ToArray
    /// iterator wrapper chain. Shared with <see cref="RegisteredSubjectMember"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Attribute[] AppendAttribute(Attribute[] attributes, Attribute extra)
    {
        var combined = new Attribute[attributes.Length + 1];
        Array.Copy(attributes, combined, attributes.Length);
        combined[attributes.Length] = extra;
        return combined;
    }
}

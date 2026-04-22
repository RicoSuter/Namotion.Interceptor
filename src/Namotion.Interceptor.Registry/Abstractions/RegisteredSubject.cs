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

    // Primary storage for all registered members (properties, attributes, and methods).
    // Writers build a new FrozenDictionary under _lock and assign atomically;
    // readers observe a consistent snapshot via the volatile reference read.
    private volatile FrozenDictionary<string, RegisteredSubjectMember> _members;

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
    /// Gets all registered members (properties, attributes, and methods).
    /// </summary>
    public IReadOnlyDictionary<string, RegisteredSubjectMember> Members => _members;

    /// <summary>
    /// Gets all registered properties (attributes are excluded; access via <see cref="RegisteredSubjectMember.Attributes"/>).
    /// </summary>
    /// <remarks>
    /// Returns a freshly computed snapshot on each access (filters the underlying
    /// members dictionary). Callers on hot paths should cache the result in a local
    /// rather than re-read the property in a tight loop.
    /// </remarks>
    public ImmutableArray<RegisteredSubjectProperty> Properties
    {
        get
        {
            var builder = ImmutableArray.CreateBuilder<RegisteredSubjectProperty>();
            foreach (var member in _members.Values)
            {
                if (member is RegisteredSubjectProperty property and not RegisteredSubjectAttribute)
                {
                    builder.Add(property);
                }
            }
            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Gets all registered methods.
    /// </summary>
    /// <remarks>
    /// Returns a freshly computed snapshot on each access (filters the underlying
    /// members dictionary). Callers on hot paths should cache the result in a local
    /// rather than re-read the property in a tight loop.
    /// </remarks>
    public ImmutableArray<RegisteredSubjectMethod> Methods
    {
        get
        {
            var builder = ImmutableArray.CreateBuilder<RegisteredSubjectMethod>();
            foreach (var member in _members.Values)
            {
                if (member is RegisteredSubjectMethod method)
                {
                    builder.Add(method);
                }
            }
            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Gets all attributes that are attached to a member.
    /// </summary>
    internal RegisteredSubjectAttribute[] GetMemberAttributes(string memberName)
    {
        var result = new List<RegisteredSubjectAttribute>();
        foreach (var member in _members.Values)
        {
            if (member is RegisteredSubjectAttribute attribute &&
                attribute.MemberName == memberName)
            {
                result.Add(attribute);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Gets a member attribute by name.
    /// </summary>
    internal RegisteredSubjectAttribute? TryGetMemberAttribute(string memberName, string attributeName)
    {
        foreach (var member in _members.Values)
        {
            if (member is RegisteredSubjectAttribute attribute &&
                attribute.MemberName == memberName &&
                attribute.AttributeName == attributeName)
            {
                return attribute;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a member by name (property or method).
    /// </summary>
    /// <param name="memberName">The member name.</param>
    /// <returns>The member or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectMember? TryGetMember(string memberName)
    {
        return _members.GetValueOrDefault(memberName);
    }

    /// <summary>
    /// Gets the property with the given name.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The property or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetProperty(string propertyName)
    {
        return _members.GetValueOrDefault(propertyName) as RegisteredSubjectProperty;
    }

    /// <summary>
    /// Gets the method with the given name.
    /// </summary>
    /// <param name="methodName">The method name.</param>
    /// <returns>The method or null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectMethod? TryGetMethod(string methodName)
    {
        return _members.GetValueOrDefault(methodName) as RegisteredSubjectMethod;
    }

    /// <summary>
    /// Initializes a new <see cref="RegisteredSubject"/> and populates its members
    /// from the subject's properties and methods.
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    public RegisteredSubject(IInterceptorSubject subject)
    {
        Subject = subject;

        var members = new Dictionary<string, RegisteredSubjectMember>();

        foreach (var property in subject.Properties)
        {
            members[property.Key] = RegisteredSubjectProperty.Create(
                this, property.Key, property.Value.Type, property.Value.Attributes);
        }

        foreach (var method in subject.Methods)
        {
            members[method.Key] = new RegisteredSubjectMethod(this, method.Key, method.Value);
        }

        _members = members.ToFrozenDictionary();
    }

    /// <summary>
    /// Adds a dynamic method to the subject.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <param name="returnType">The return type.</param>
    /// <param name="parameters">The parameter metadata.</param>
    /// <param name="invoke">The invoke delegate.</param>
    /// <param name="attributes">The .NET reflection attributes.</param>
    /// <returns>The created method.</returns>
    public RegisteredSubjectMethod AddMethod(
        string name,
        Type returnType,
        IReadOnlyList<SubjectMethodParameterMetadata> parameters,
        Func<IInterceptorSubject, object?[], object?> invoke,
        params Attribute[] attributes)
    {
        var metadata = new SubjectMethodMetadata(
            name, returnType, parameters, attributes, invoke,
            isIntercepted: false, isDynamic: true, isPublic: true);

        Subject.AddMethods([metadata]);

        var method = new RegisteredSubjectMethod(this, name, metadata);

        lock (_lock)
        {
            // Methods never carry MemberAttributeAttribute, so no AttributesCache
            // on existing members needs to be invalidated here.
            _members = _members
                .Append(KeyValuePair.Create<string, RegisteredSubjectMember>(method.Name, method))
                .ToFrozenDictionary();
        }

        // Run method initializers outside the lock so they can safely call back
        // into the registry. Mirrors the attach-path behavior in SubjectRegistry.
        method.RunInitializers();

        return method;
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
            attributes.Concat([new DerivedAttribute()]).ToArray());
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
        return AddProperty(name, type, getValue, setValue, attributes
            .Concat([new DerivedAttribute()]).ToArray());
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

        // Trigger the initial change event with CurrentValue=null to indicate a new property.
        // Using null (not readValue) ensures interceptors see a null→value transition,
        // which is correct for lifecycle tracking of subjects in the initial value.
        property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null,
            null, delegate { });

        return property;
    }

    private RegisteredSubjectProperty AddPropertyInternal(string name, Type type, Attribute[] attributes)
    {
        var subjectProperty = RegisteredSubjectProperty.Create(this, name, type, attributes);

        lock (_lock)
        {
            _members = _members
                .Append(KeyValuePair.Create<string, RegisteredSubjectMember>(subjectProperty.Name, subjectProperty))
                .ToFrozenDictionary();

            // Targeted invalidation: if the new property is an attribute, only the
            // target member's AttributesCache must be cleared. Other members'
            // attribute lists are unaffected.
            if (subjectProperty is RegisteredSubjectAttribute attribute
                && _members.TryGetValue(attribute.MemberName, out var targetMember))
            {
                targetMember.AttributesCache = null;
            }
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);
        return subjectProperty;
    }

}

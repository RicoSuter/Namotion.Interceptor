using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubject
{
    private readonly Lock _lock = new();

    private volatile FrozenDictionary<string, RegisteredSubjectProperty> _properties;

    private SubjectPropertyParent[] _parentsSnapshot = [];

    [JsonIgnore] public IInterceptorSubject Subject { get; }

    internal IInterceptorSubjectContext? Context { get; }

    internal long AttachmentRevision { get; }

    /// <summary>
    /// Gets the current reference count (number of parent references), or 0 when the subject is
    /// not attached, because no edge can point at an unattached subject.
    /// </summary>
    public int ReferenceCount => Subject.GetReferenceCount();

    /// <summary>
    /// Gets the properties which reference this subject.
    /// Thread-safe; reads are lock-free once the snapshot is cached.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> Parents
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _parentsSnapshot));
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

    public RegisteredSubject(IInterceptorSubject subject) : this(subject, [.. subject.Properties.Values], null, 0)
    {
    }

    internal RegisteredSubject(
        IInterceptorSubject subject,
        ImmutableArray<SubjectPropertyMetadata> properties,
        IInterceptorSubjectContext? context,
        long attachmentRevision)
    {
        Subject = subject;
        Context = context;
        AttachmentRevision = attachmentRevision;
        _properties = properties
            .ToFrozenDictionary(
                metadata => metadata.Name,
                metadata => new RegisteredSubjectProperty(
                    this, metadata.Name, metadata.Type, metadata.Attributes));
    }

    internal void ApplyProjection(
        ImmutableArray<SubjectPropertyMetadata> properties,
        ImmutableArray<SubjectPropertyParent> parents)
    {
        lock (_lock)
        {
            var projections = _properties.ToDictionary();
            foreach (var metadata in properties)
            {
                if (!projections.ContainsKey(metadata.Name))
                {
                    projections.Add(metadata.Name, new RegisteredSubjectProperty(
                        this, metadata.Name, metadata.Type, metadata.Attributes));
                }
            }
            _properties = projections.ToFrozenDictionary();
            foreach (var property in _properties.Values)
            {
                property.AttributesCache = null;
            }
            Volatile.Write(ref _parentsSnapshot, parents.ToArray());
        }
    }

    internal void ReplaceParents(ImmutableArray<SubjectPropertyParent> parents)
    {
        lock (_lock)
        {
            Volatile.Write(ref _parentsSnapshot, parents.ToArray());
        }
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
        if (Subject.Properties.TryGetValue(name, out var existingMetadata))
        {
            // The subject keeps its metadata across detach and reattach while Registry's
            // projection is rebuilt per attach, and initializers rerun their AddProperty on every
            // attach. Admission rejects a name the subject already carries, so without this the
            // rerun would throw and no subject holding a dynamic property could reattach.
            //
            // The first registration wins and the caller's delegates are discarded, rather than
            // the last: the metadata backs captured RegisteredSubjectProperty projections and the
            // committed baselines keyed off it, so swapping accessors underneath a live subject
            // would leave both resolving through delegates their registration never saw. A
            // different shape is a genuine duplicate and is rejected like any duplicate name.
            if (existingMetadata.Type != type || !AttributesMatch(existingMetadata.Attributes, attributes))
            {
                throw new InvalidOperationException(
                    $"A property named '{name}' is already defined on the subject " +
                    $"'{Subject.GetType().Name}' with a different shape. Only an identically " +
                    "shaped re-registration, the reattach case, is supported.");
            }

            var existingProperty = GetOrAddPropertyProjection(name, existingMetadata.Type, existingMetadata.Attributes);
            PublishInitialValue(existingProperty);
            return existingProperty;
        }

        Subject.AddProperties(new SubjectPropertyMetadata(
            name,
            type,
            attributes,
            getValue is null
                ? null
                : type.CanContainSubjects()
                    ? s => ((InterceptorExecutor)s.Executor).GetGeneratedPropertyValue(name, getValue)
                    : s => s.Executor.GetPropertyValue(name, getValue),
            setValue is not null
                // The value arrives boxed here, so a TProperty-routed write would classify every
                // dynamic property as structural and put scalar writes (source telemetry, say)
                // through the lifecycle gate on every update. The declared type is known at
                // registration time, so the setter is built once to call the typed write entry
                // with that type as the generic argument, agreeing with how the lifecycle
                // classifies the property inside the chain.
                ? TypedPropertyWriteFactory.CreateSetter(type, name, getValue, setValue)
                : null,
            isIntercepted: true,
            isDynamic: true));

        var property = GetOrAddPropertyProjection(name, type, attributes);

        // No explicit callback fan-out here. An attached subject's admission already invoked the
        // property lifecycle callbacks, and SubjectRegistry.AttachProperty created this projection
        // from inside that fan-out. An unattached subject resolves no context and therefore no
        // handlers, so there is nothing to notify.
        PublishInitialValue(property);
        return property;
    }

    /// <summary>
    /// Publishes a dynamic property's initial value as a null-to-value write: the transition from
    /// "the property did not exist" to what it now holds. Nothing else reports that to change
    /// tracking or to the other write interceptors.
    /// </summary>
    /// <remarks>
    /// Fires on every registration, including the re-registration an initializer performs on each
    /// attach, because a reattached subject presents its dynamic properties to the graph afresh and
    /// an observer that missed the first registration has no other way to learn their values. The
    /// value is already in the caller's backing store, so the write carries a no-op writer and only
    /// traverses the chain.
    ///
    /// A property that can hold subjects is excluded: admission captures and commits its initial
    /// value, which left this write a no-op traversal that could only throw, through the callback
    /// write guard, when a lifecycle handler added one.
    /// </remarks>
    private void PublishInitialValue(RegisteredSubjectProperty property)
    {
        if (property.Type.CanContainSubjects())
        {
            return;
        }

        TypedPropertyWriteFactory
            .CreateSetter(property.Type, property.Name, getValue: null, setValue: static (_, _) => { })
            .Invoke(Subject, property.GetValue());
    }

    /// <summary>
    /// Whether a re-registration carries the same observable shape as the existing metadata.
    /// Accessor delegates cannot be compared (initializers create fresh closures on every
    /// attach), so shape is the declared type and the attribute list, compared pairwise with
    /// <see cref="Attribute"/> value equality.
    /// </summary>
    private static bool AttributesMatch(IReadOnlyCollection<Attribute> existingAttributes, Attribute[] requestedAttributes)
    {
        if (existingAttributes.Count != requestedAttributes.Length)
        {
            return false;
        }

        var index = 0;
        foreach (var attribute in existingAttributes)
        {
            if (!Equals(attribute, requestedAttributes[index++]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the projection for the named property, creating and publishing it when missing. Called
    /// by <c>AddProperty</c> and, for properties admitted through
    /// <see cref="IInterceptorSubject.AddProperties"/>, by the registry's property attach callback,
    /// which is what makes the projection exist before the property's initial structural edge
    /// notifications resolve it.
    /// </summary>
    internal RegisteredSubjectProperty GetOrAddPropertyProjection(string name, Type type, IReadOnlyCollection<Attribute> attributes)
    {
        lock (_lock)
        {
            if (_properties.TryGetValue(name, out var existingProperty))
            {
                return existingProperty;
            }

            var subjectProperty = new RegisteredSubjectProperty(this, name, type, attributes);
            var newProperties = _properties
                .Append(KeyValuePair.Create(subjectProperty.Name, subjectProperty))
                .ToFrozenDictionary(p => p.Key, p => p.Value);

            _properties = newProperties;

            foreach (var property in newProperties.Values)
            {
                property.AttributesCache = null;
            }

            return subjectProperty;
        }
    }
}

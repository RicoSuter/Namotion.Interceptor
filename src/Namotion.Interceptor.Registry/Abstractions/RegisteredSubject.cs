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
    private sealed class PropertyStorage
    {
        public readonly Dictionary<string, RegisteredSubjectProperty> Dictionary;
        public readonly ImmutableArray<RegisteredSubjectProperty> Array;

        public PropertyStorage(
            Dictionary<string, RegisteredSubjectProperty> dictionary,
            ImmutableArray<RegisteredSubjectProperty> array)
        {
            Dictionary = dictionary;
            Array = array;
        }
    }

    private readonly Lock _parentsLock = new();

    private volatile PropertyStorage _storage;
    private ImmutableArray<SubjectPropertyParent> _parents = [];

    [JsonIgnore] public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Gets the current reference count (number of parent references).
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public int ReferenceCount => Subject.GetReferenceCount();

    /// <summary>
    /// Gets the properties which reference this subject.
    /// Thread-safe: Lock ensures atomic struct copy during read.
    /// </summary>
    public ImmutableArray<SubjectPropertyParent> Parents
    {
        get
        {
            lock (_parentsLock)
                return _parents;
        }
    }

    /// <summary>
    /// Gets all registered properties of the subject.
    /// Allocation-free: Returns cached ImmutableArray.
    /// </summary>
    public ImmutableArray<RegisteredSubjectProperty> Properties
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _storage.Array;
    }

    /// <summary>
    /// Gets all attributes which are attached to this property.
    /// </summary>
    public IEnumerable<RegisteredSubjectProperty> GetPropertyAttributes(string propertyName)
    {
        var storage = _storage;
        foreach (var property in storage.Array)
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
        var storage = _storage;
        foreach (var property in storage.Array)
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
        return _storage.Dictionary.GetValueOrDefault(propertyName);
    }

    public RegisteredSubject(IInterceptorSubject subject)
    {
        Subject = subject;

        var count = subject.Properties.Count;
        var array = new RegisteredSubjectProperty[count];
        var dictionary = new Dictionary<string, RegisteredSubjectProperty>(count);

        var index = 0;
        foreach (var p in subject.Properties)
        {
            var prop = new RegisteredSubjectProperty(this, p.Key, p.Value.Type, p.Value.Attributes);
            array[index++] = prop;
            dictionary[p.Key] = prop;
        }

        _storage = new PropertyStorage(dictionary, ImmutableCollectionsMarshal.AsImmutableArray(array));
    }

    internal void AddParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_parentsLock)
        {
            _parents = _parents.Add(new SubjectPropertyParent { Property = parent, Index = index });
        }
    }

    internal void RemoveParent(RegisteredSubjectProperty parent, object? index)
    {
        lock (_parentsLock)
        {
            _parents = _parents.Remove(new SubjectPropertyParent { Property = parent, Index = index });
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
        Subject.AddProperties(new SubjectPropertyMetadata(
            name,
            type,
            attributes,
            getValue is not null ? s => ((IInterceptorExecutor)s.Context).GetPropertyValue(name, getValue) : null,
            setValue is not null ? (s, v) => ((IInterceptorExecutor)s.Context).SetPropertyValue(name, v, getValue, setValue) : null,
            isIntercepted: true,
            isDynamic: true));

        var property = AddPropertyInternal(name, type, attributes);

        // trigger change event
        property.Reference.SetPropertyValueWithInterception(getValue?.Invoke(Subject) ?? null,
            o => getValue?.Invoke(o), delegate { });

        return property;
    }

    private RegisteredSubjectProperty AddPropertyInternal(string name, Type type, Attribute[] attributes)
    {
        var subjectProperty = new RegisteredSubjectProperty(this, name, type, attributes);

        var currentStorage = _storage;
        var newDictionary = new Dictionary<string, RegisteredSubjectProperty>(currentStorage.Dictionary)
        {
            [subjectProperty.Name] = subjectProperty
        };
        var newArray = currentStorage.Array.Add(subjectProperty);

        _storage = new PropertyStorage(newDictionary, newArray);

        foreach (var property in newArray)
        {
            property.AttributesCache = null;
        }

        Subject.AttachSubjectProperty(subjectProperty.Reference);
        return subjectProperty;
    }
}

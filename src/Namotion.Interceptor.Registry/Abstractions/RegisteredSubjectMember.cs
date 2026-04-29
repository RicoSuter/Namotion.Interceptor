using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

public abstract class RegisteredSubjectMember
{
    // Initialized to an empty array so a freshly-constructed member is safe to
    // observe even before the writer publishes the real cache. Subsequent
    // assignments happen under RegisteredSubject._lock; the volatile write
    // publishes the new array to readers on weak memory models.
    internal volatile RegisteredSubjectAttribute[] AttributesCache = Array.Empty<RegisteredSubjectAttribute>();

    protected RegisteredSubjectMember(
        RegisteredSubject parent,
        string name,
        IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        Parent = parent;
        Name = name;
        ReflectionAttributes = reflectionAttributes;
    }

    /// <summary>
    /// Gets the parent subject which contains the member.
    /// </summary>
    public RegisteredSubject Parent { get; }

    /// <summary>
    /// Gets the name of the member.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the browse name of the member.
    /// </summary>
    public virtual string BrowseName => Name;

    /// <summary>
    /// Gets all .NET reflection attributes for this member.
    /// </summary>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; }

    /// <summary>
    /// Gets all attributes which are attached to this member.
    /// </summary>
    public RegisteredSubjectAttribute[] Attributes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AttributesCache;
    }

    /// <summary>
    /// Gets a member attribute by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectAttribute? TryGetAttribute(string attributeName)
    {
        var attributes = AttributesCache;
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeName == attributeName)
                return attribute;
        }
        return null;
    }

    /// <summary>
    /// Adds an attribute to the member.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectAttribute AddAttribute(
        string name, Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue,
        params Attribute[] attributes)
    {
        var propertyName = $"{Name}@{name}";
        var combined = RegisteredSubject.AppendAttribute(attributes, new PropertyAttributeAttribute(Name, name));

        var attribute = Parent.AddProperty(propertyName, type, getValue, setValue, combined);
        return (RegisteredSubjectAttribute)attribute;
    }

    /// <summary>
    /// Adds a derived attribute to the member.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectAttribute AddDerivedAttribute(
        string name, Type type,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue,
        params Attribute[] attributes)
    {
        var propertyName = $"{Name}@{name}";
        var combined = RegisteredSubject.AppendAttribute(attributes, new PropertyAttributeAttribute(Name, name));

        var attribute = Parent.AddDerivedProperty(propertyName, type, getValue, setValue, combined);
        return (RegisteredSubjectAttribute)attribute;
    }
}

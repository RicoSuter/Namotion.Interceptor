using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Base for any registered member of a subject (currently <see cref="RegisteredSubjectProperty"/>
/// and its <see cref="RegisteredSubjectAttribute"/> subclass; future kinds such as registered
/// methods slot in here without storage changes to <see cref="RegisteredSubject"/>).
/// </summary>
public abstract class RegisteredSubjectMember
{
    internal RegisteredSubjectAttribute[]? AttributesCache; // TODO: Dangerous cache, needs review

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    /// Gets the browse name of the member. Overridden in <see cref="RegisteredSubjectAttribute"/>
    /// to return the attribute name.
    /// </summary>
    public virtual string BrowseName => Name;

    /// <summary>
    /// Gets all .NET reflection attributes for this member, including inherited attributes.
    /// </summary>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; }

    /// <summary>
    /// Gets all attributes which are attached to this member.
    /// </summary>
    public RegisteredSubjectAttribute[] Attributes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AttributesCache = (AttributesCache ?? Parent.GetPropertyAttributes(Name).ToArray());
    }

    /// <summary>
    /// Gets an attribute of this member by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute, or null if no such attribute exists.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectAttribute? TryGetAttribute(string attributeName)
    {
        return Parent.TryGetPropertyAttribute(Name, attributeName);
    }

    /// <summary>
    /// Adds an attribute to the member.
    /// </summary>
    public RegisteredSubjectAttribute AddAttribute<TProperty>(
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
    /// Adds an attribute to the member.
    /// </summary>
    public RegisteredSubjectAttribute AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), getValue, setValue, attributes);
    }

    /// <summary>
    /// Adds an attribute to the member.
    /// </summary>
    public RegisteredSubjectAttribute AddAttribute(
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

        return (RegisteredSubjectAttribute)attribute;
    }

    /// <summary>
    /// Adds a derived attribute to the member.
    /// </summary>
    public RegisteredSubjectAttribute AddDerivedAttribute(
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

        return (RegisteredSubjectAttribute)attribute;
    }
}

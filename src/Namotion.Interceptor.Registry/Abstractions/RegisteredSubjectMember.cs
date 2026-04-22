using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

public abstract class RegisteredSubjectMember
{
    // Volatile: lock-free readers of member.Attributes must observe null-writes
    // paired with the volatile _members writes on RegisteredSubject via acquire/release
    // semantics. Without volatile, a reader could observe a stale cache after the
    // new _members snapshot.
    //
    // Write ordering on the producer side matters: in AddPropertyInternal the
    // null-write to this field happens AFTER the _members volatile write, both
    // under _lock. This guarantees that any reader which subsequently observes
    // cache == null and recomputes attributes from _members always computes
    // from the updated snapshot. Swapping the order (null first, _members second)
    // would introduce a wedging hazard: a reader racing between the two writes
    // could cache the old attribute list derived from the old _members, with
    // no further invalidation signal pending.
    internal volatile RegisteredSubjectAttribute[]? AttributesCache;

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
        get => AttributesCache ??= Parent.GetMemberAttributes(Name);
    }

    /// <summary>
    /// Gets a member attribute by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectAttribute? TryGetAttribute(string attributeName)
    {
        return Parent.TryGetMemberAttribute(Name, attributeName);
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

        var attribute = Parent.AddProperty(
            propertyName, type, getValue, setValue,
            attributes
                .Concat([new MemberAttributeAttribute(Name, name)])
                .ToArray());

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

        var attribute = Parent.AddDerivedProperty(
            propertyName, type, getValue, setValue,
            attributes
                .Concat([new MemberAttributeAttribute(Name, name)])
                .ToArray());

        return (RegisteredSubjectAttribute)attribute;
    }
}

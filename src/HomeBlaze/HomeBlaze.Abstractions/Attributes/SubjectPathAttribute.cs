namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a string property as a path. Without a type the path leads to what <see cref="Kind"/> allows, and with a type
/// to a subject of that type. An editor lets the user pick the path as well as type it, and the property panel shows a
/// <see cref="StateAttribute"/> property with a type as the subject at its canonical path, with a link to it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SubjectPathAttribute : Attribute
{
    public SubjectPathAttribute()
    {
    }

    /// <param name="subjectType">The type that the subject at the path is an instance of.</param>
    public SubjectPathAttribute(Type subjectType)
    {
        SubjectType = subjectType;
    }

    /// <summary>
    /// The type that the subject at the path is an instance of, or null when the path leads to no subject.
    /// </summary>
    public Type? SubjectType { get; }

    /// <summary>What a path that leads to no subject may lead to. Ignored when <see cref="SubjectType"/> is set.</summary>
    public SubjectPathKind Kind { get; set; } = SubjectPathKind.Property;

    /// <summary>
    /// The types that the value of a property or an attribute at the path may have, or null for any type. A type, a
    /// nullable type as its underlying type, matches when it is assignable to one of them or, for an open generic type,
    /// implements or derives from a closed form of it.
    /// <see cref="Enum"/> matches every enum, and <see cref="char"/> matches no interface of
    /// <c>System.Numerics</c>. <c>typeof(INumber&lt;&gt;)</c> matches only the built-in integral types,
    /// <see cref="float"/>, <see cref="double"/> and <see cref="decimal"/>, the numbers that number widgets read. Does not
    /// restrict an operation. Ignored when <see cref="SubjectType"/> is set.
    /// </summary>
    public Type[]? ValueTypes { get; set; }

    /// <summary>
    /// Whether only a property or an attribute with a public setter may be selected, never an operation. Ignored when
    /// <see cref="SubjectType"/> is set.
    /// </summary>
    public bool IsWritable { get; set; }
}

namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a string property as a path. Without a type the path leads to a property, and with a type to a subject of
/// that type. An editor lets the user pick the path as well as type it, and the property panel shows a
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
    /// The type that the subject at the path is an instance of, or null when the path leads to a property.
    /// </summary>
    public Type? SubjectType { get; }

    /// <summary>What a path to a property may lead to. Flags combine with OR. Ignored when <see cref="SubjectType"/> is set.</summary>
    public SubjectPathKinds Kinds { get; set; } = SubjectPathKinds.Value;

    /// <summary>Whether only properties with a public setter may be selected. Combined with <see cref="Kinds"/> with AND. Ignored when <see cref="SubjectType"/> is set.</summary>
    public bool IsWritable { get; set; }
}

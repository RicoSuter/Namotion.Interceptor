namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a string property as a path, so that an editor lets the user pick the path as well as type it.
/// Without a type the path leads to a property, and with a type to a subject of that type.
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
}

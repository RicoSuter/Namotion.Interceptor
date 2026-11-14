namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// What a path may lead to, see <see cref="SubjectPathAttribute.Kind"/>. Flags combine with OR.
/// </summary>
[Flags]
public enum SubjectPathKind
{
    /// <summary>A property that holds a value and is neither an attribute nor an operation.</summary>
    Property = 1,

    /// <summary>An attribute of a property or of an operation, such as its minimum.</summary>
    Attribute = 2,

    /// <summary>An operation or a query: a property whose value is a <see cref="Metadata.MethodMetadata"/>.</summary>
    Operation = 4,
}

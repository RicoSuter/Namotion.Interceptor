namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// What a path to a property may lead to, see <see cref="SubjectPathAttribute.Kinds"/>. Flags combine with OR.
/// A nullable type counts as its underlying type.
/// </summary>
[Flags]
public enum SubjectPathKinds
{
    /// <summary>A <see cref="bool"/>.</summary>
    Boolean = 1,

    /// <summary>An integral, floating point or decimal number.</summary>
    Number = 2,

    /// <summary>An enum.</summary>
    Enum = 4,

    /// <summary>Any other value, for example a string or a date.</summary>
    OtherValue = 8,

    /// <summary>An operation or a query: a property whose value is a <see cref="Metadata.MethodMetadata"/>.</summary>
    Operation = 16,

    /// <summary>Any value, but no operation.</summary>
    Value = Boolean | Number | Enum | OtherValue,
}

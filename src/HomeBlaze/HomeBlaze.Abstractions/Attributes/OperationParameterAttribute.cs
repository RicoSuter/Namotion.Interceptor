namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Attribute to specify metadata for operation method parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
public class OperationParameterAttribute : Attribute
{
    /// <summary>
    /// The unit of the parameter value (e.g., Percent, DegreeCelsius).
    /// </summary>
    public StateUnit Unit { get; set; }

    /// <summary>
    /// The name of the editor that the user interface enters the value with, such as a picker of role names, or null for
    /// the field of the parameter type. A user interface without an editor of that name uses the field of the type.
    /// </summary>
    public string? Editor { get; set; }

    /// <summary>
    /// The name of a property of the subject of the method whose current value prefills the parameter in the user
    /// interface, such as the current roles of a user for an operation that replaces them, or null. The value is
    /// prefilled only when the person may read the property.
    /// </summary>
    public string? InitialValueProperty { get; set; }
}

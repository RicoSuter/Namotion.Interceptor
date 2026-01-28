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
}

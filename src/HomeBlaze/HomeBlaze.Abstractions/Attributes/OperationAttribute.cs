namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a method as an operation that can be invoked from the UI.
/// Operations are methods with side effects (e.g., TurnOn(), Reset()).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class OperationAttribute : SubjectMethodAttribute
{
    /// <summary>
    /// If true, shows a confirmation dialog before executing.
    /// Use for dangerous or irreversible operations.
    /// Such operations cannot be invoked through MCP, by agents or external clients. A method that implements an
    /// interface operation or overrides a base class operation that sets this keeps the requirement and is an
    /// operation, whatever its own attribute says.
    /// </summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// If true, the operation changes what the application is or who may use it, such as creating subjects or managing
    /// users. It is never run through <c>execute</c>: a person needs the <c>administer</c> action on it, and agents and
    /// MCP clients cannot run it. A method that implements an interface operation or overrides a base class operation
    /// that sets this keeps the requirement.
    /// </summary>
    public bool RequiresAdministration { get; set; }

    /// <summary>
    /// What the operation is for; <see cref="OperationPurpose.None"/> by default. Independent of
    /// <see cref="RequiresConfirmation"/>, which still decides whether agents may call the operation.
    /// <see cref="OperationPurpose.Delete"/> is ignored on an operation with a parameter that requires input.
    /// </summary>
    public OperationPurpose Purpose { get; set; }
}

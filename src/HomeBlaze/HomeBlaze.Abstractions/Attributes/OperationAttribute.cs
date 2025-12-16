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
    /// </summary>
    public bool RequiresConfirmation { get; set; }
}

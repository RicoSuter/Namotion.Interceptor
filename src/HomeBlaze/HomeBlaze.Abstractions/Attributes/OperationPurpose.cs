namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// What an operation is for, so that a user interface can show it as a built-in control instead of as an operation
/// button.
/// </summary>
public enum OperationPurpose
{
    /// <summary>
    /// An ordinary operation.
    /// </summary>
    None = 0,

    /// <summary>
    /// Deletes or removes the subject. The subject browser shows it as its Delete button and asks for confirmation
    /// before it invokes it. Takes no parameter that requires input.
    /// </summary>
    Delete = 1,
}

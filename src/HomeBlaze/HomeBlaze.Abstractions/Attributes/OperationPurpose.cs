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

    /// <summary>
    /// Saves what the edit component of the subject edits. The subject browser shows it as its Edit button, which opens the
    /// edit component, and the component saves by invoking it with the edited values as its parameters, through the same
    /// permission check as any other operation, so that the save is decided by the permissions on this operation. The edit
    /// component writes only through this operation. It decides the save only for a subject without configuration
    /// members; one with configuration members needs <c>administer</c> on all of them, as without this operation.
    /// </summary>
    Edit = 2,
}

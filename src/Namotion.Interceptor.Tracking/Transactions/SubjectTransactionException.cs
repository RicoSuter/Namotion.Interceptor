using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction fails to commit.
/// </summary>
public class SubjectTransactionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectTransactionException"/> class.
    /// </summary>
    public SubjectTransactionException(
        string message,
        IReadOnlyList<SubjectPropertyChange> appliedChanges,
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors)
        : this(message, appliedChanges, failedChanges, errors, CreateInnerException(errors))
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit inner exception, for subclasses.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="appliedChanges">The changes that were applied.</param>
    /// <param name="failedChanges">The changes that did not commit.</param>
    /// <param name="errors">The errors that occurred.</param>
    /// <param name="innerException">The inner exception, or null.</param>
    protected SubjectTransactionException(
        string message,
        IReadOnlyList<SubjectPropertyChange> appliedChanges,
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors,
        Exception? innerException)
        : base(message, innerException)
    {
        AppliedChanges = appliedChanges;
        FailedChanges = failedChanges;
        Errors = errors;
    }

    /// <summary>
    /// Gets the changes that were successfully written to source and applied to local model.
    /// Entries carry the source that accepted the write, not the capture-time source.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> AppliedChanges { get; }

    /// <summary>
    /// Gets the changes that did not commit. Which changes are listed depends on the failure handling, the failed
    /// stage and whether <see cref="ITransactionHandler"/>s checked the changes, see the Failure Table in
    /// docs/tracking-transactions.md. A change can appear more than once, for example a property whose revert failed.
    /// The <see cref="SubjectPropertyChange.Origin"/> of an entry carries no guarantee here: terminal writer failures
    /// may leave the snapshot partially marked. Use <see cref="AppliedChanges"/> to see what landed.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the errors that occurred during commit. Not aligned one to one with <see cref="FailedChanges"/>:
    /// a failed source write or revert yields one error covering all of that source's changes, while a failed
    /// local apply or revert yields one error per change, so this list can be shorter.
    /// </summary>
    public IReadOnlyList<Exception> Errors { get; }

    /// <summary>
    /// Gets a value indicating whether the commit was a partial success: at least one change was applied
    /// and at least one change failed.
    /// </summary>
    public bool IsPartialSuccess => AppliedChanges.Count > 0 && FailedChanges.Count > 0;

    private static Exception? CreateInnerException(IReadOnlyList<Exception> errors)
    {
        return errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException("Multiple source write failures occurred.", errors)
        };
    }
}

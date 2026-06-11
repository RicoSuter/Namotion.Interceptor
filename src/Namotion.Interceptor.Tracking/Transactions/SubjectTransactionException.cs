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
        : base(message, CreateInnerException(errors))
    {
        AppliedChanges = appliedChanges;
        FailedChanges = failedChanges;
        Errors = errors;
    }

    /// <summary>
    /// Gets the changes that were successfully written to source and applied to local model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> AppliedChanges { get; }

    /// <summary>
    /// Gets the changes that did not commit: source-write failures, changes that threw while being applied
    /// to or reverted from the local model, and, under <see cref="TransactionFailureHandling.Rollback"/>,
    /// local (no-source) changes that were never applied. A change can appear more than once if it failed at
    /// more than one stage. The <see cref="SubjectPropertyChange.Source"/> of an entry carries no guarantee
    /// here: on terminal writer failures the snapshot may already be partially marked by accepting sources,
    /// so a non-null source does not mean the change was applied or reverted.
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

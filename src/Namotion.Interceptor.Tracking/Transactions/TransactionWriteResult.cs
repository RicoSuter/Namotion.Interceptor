using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of external write operations during transaction commit.
/// </summary>
/// <param name="SuccessfulChanges">The changes that were successfully written to external sources and applied to the in-process model.</param>
/// <param name="FailedChanges">The changes that failed to write to external sources.</param>
/// <param name="Errors">The errors that occurred during writing, one per source that failed.</param>
/// <param name="LocalChanges">The changes that the writer did not handle (no associated source). The caller is responsible for applying these to the in-process model.</param>
public readonly record struct TransactionWriteResult(
    IReadOnlyList<SubjectPropertyChange> SuccessfulChanges,
    IReadOnlyList<SubjectPropertyChange> FailedChanges,
    IReadOnlyList<Exception> Errors,
    IReadOnlyList<SubjectPropertyChange> LocalChanges)
{
    /// <summary>
    /// Creates a successful result where all changes were written.
    /// </summary>
    /// <param name="changes">The changes that were successfully written.</param>
    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, [], [], []);
}

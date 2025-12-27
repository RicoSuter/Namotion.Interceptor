using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of external write operations during transaction commit.
/// </summary>
public class TransactionWriteResult
{
    /// <summary>
    /// Gets the changes that were successfully written to external sources
    /// and applied to the in-process model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> SuccessfulChanges { get; }

    /// <summary>
    /// Gets the changes that failed to write to external sources.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the changes that the writer did not handle (no associated source).
    /// The caller is responsible for applying these to the in-process model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> LocalChanges { get; }

    /// <summary>
    /// Gets the errors that occurred during writing, one per source that failed.
    /// </summary>
    public IReadOnlyList<Exception> Errors { get; }

    public TransactionWriteResult(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors,
        IReadOnlyList<SubjectPropertyChange>? localChanges = null)
    {
        SuccessfulChanges = successfulChanges;
        FailedChanges = failedChanges;
        LocalChanges = localChanges ?? [];
        Errors = errors;
    }

    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, [], []);
}

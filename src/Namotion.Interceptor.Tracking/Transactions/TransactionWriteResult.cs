using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of external write operations during transaction commit.
/// </summary>
public class TransactionWriteResult
{
    /// <summary>
    /// Gets the changes that were successfully written.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> SuccessfulChanges { get; }

    /// <summary>
    /// Gets the changes that failed to write.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the errors that occurred during writing, one per source that failed.
    /// </summary>
    public IReadOnlyList<Exception> Errors { get; }

    public TransactionWriteResult(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        IReadOnlyList<SubjectPropertyChange> failedChanges,
        IReadOnlyList<Exception> errors)
    {
        SuccessfulChanges = successfulChanges;
        FailedChanges = failedChanges;
        Errors = errors;
    }

    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, [], []);
}

using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of external write operations during transaction commit.
/// </summary>
public class TransactionWriteResult
{
    /// <summary>
    /// Gets the successful changes.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> SuccessfulChanges { get; }

    /// <summary>
    /// Gets the failed write operations with detailed failure information.
    /// </summary>
    public IReadOnlyList<SourceWriteFailure> FailedChanges { get; }

    public TransactionWriteResult(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        IReadOnlyList<SourceWriteFailure> failedChanges)
    {
        SuccessfulChanges = successfulChanges;
        FailedChanges = failedChanges;
    }

    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, Array.Empty<SourceWriteFailure>());
}

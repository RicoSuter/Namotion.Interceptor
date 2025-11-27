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
    /// Gets the exceptions that occurred during write operations.
    /// </summary>
    public IReadOnlyList<Exception> Failures { get; }

    public TransactionWriteResult(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        IReadOnlyList<Exception> failures)
    {
        SuccessfulChanges = successfulChanges;
        Failures = failures;
    }

    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, Array.Empty<Exception>());
}

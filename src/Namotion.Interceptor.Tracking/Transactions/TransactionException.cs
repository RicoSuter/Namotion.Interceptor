using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction fails to commit.
/// </summary>
public class TransactionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionException"/> class.
    /// </summary>
    public TransactionException(
        string message,
        IReadOnlyList<SubjectPropertyChange> appliedChanges,
        IReadOnlyList<SourceWriteFailure> failedChanges)
        : base(message)
    {
        AppliedChanges = appliedChanges;
        FailedChanges = failedChanges;
    }

    /// <summary>
    /// Gets the changes that were successfully written to source and applied to local model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> AppliedChanges { get; }

    /// <summary>
    /// Gets the changes that failed to write to source (not applied to local model).
    /// </summary>
    public IReadOnlyList<SourceWriteFailure> FailedChanges { get; }

    /// <summary>
    /// Gets a value indicating whether at least one change was applied successfully.
    /// </summary>
    public bool IsPartialSuccess => AppliedChanges.Count > 0 && FailedChanges.Count > 0;
}

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
    /// Gets the changes that failed to write to source (not applied to local model).
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the errors that occurred, one per source that failed.
    /// </summary>
    public IReadOnlyList<Exception> Errors { get; }

    /// <summary>
    /// Gets a value indicating whether at least one change was applied successfully.
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

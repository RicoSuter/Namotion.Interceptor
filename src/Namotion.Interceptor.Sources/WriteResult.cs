using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Represents the result of a write operation to an external source.
/// Contains information about which changes succeeded and any error that occurred.
/// </summary>
public readonly struct WriteResult
{
    /// <summary>
    /// Gets the changes that were successfully written to the external source.
    /// </summary>
    public ReadOnlyMemory<SubjectPropertyChange> SuccessfulChanges { get; }

    /// <summary>
    /// Gets the error that occurred during the write operation, or null if all changes succeeded.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>
    /// Gets a value indicating whether all attempted changes were successful.
    /// </summary>
    public bool IsFullySuccessful => Error is null;

    /// <summary>
    /// Gets a value indicating whether some changes succeeded (partial success).
    /// </summary>
    public bool IsPartialSuccess => Error is not null && SuccessfulChanges.Length > 0;

    /// <summary>
    /// Gets a value indicating whether no changes succeeded (complete failure).
    /// </summary>
    public bool IsCompleteFailure => Error is not null && SuccessfulChanges.Length == 0;

    private WriteResult(ReadOnlyMemory<SubjectPropertyChange> successfulChanges, Exception? error)
    {
        SuccessfulChanges = successfulChanges;
        Error = error;
    }

    /// <summary>
    /// Creates a successful result where all changes were written.
    /// </summary>
    public static WriteResult Success(ReadOnlyMemory<SubjectPropertyChange> changes) =>
        new(changes, null);

    /// <summary>
    /// Creates a partial success result where some changes were written before a failure.
    /// </summary>
    public static WriteResult PartialSuccess(ReadOnlyMemory<SubjectPropertyChange> successfulChanges, Exception error) =>
        new(successfulChanges, error);

    /// <summary>
    /// Creates a failure result where no changes were written.
    /// </summary>
    public static WriteResult Failure(Exception error) =>
        new(ReadOnlyMemory<SubjectPropertyChange>.Empty, error);
}

using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents the result of a write operation to an external source.
/// Tracks which changes failed, enabling zero-allocation on success paths.
/// </summary>
public readonly struct WriteResult
{
    /// <summary>
    /// Gets the changes that failed to write to the external source.
    /// Empty on full success.
    /// </summary>
    public ReadOnlyMemory<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the error that occurred during the write operation, or null if all changes succeeded.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>
    /// Gets a value indicating whether all attempted changes were successful.
    /// </summary>
    public bool IsFullySuccessful => Error is null;

    /// <summary>
    /// Gets a value indicating whether some changes failed but others succeeded (partial failure).
    /// </summary>
    public bool IsPartialFailure => Error is not null && FailedChanges.Length > 0;

    /// <summary>
    /// Gets a value indicating whether all changes failed (complete failure).
    /// Note: When FailedChanges is empty but Error is set, it means all input changes failed
    /// but individual failures weren't tracked (e.g., connection error before any writes).
    /// </summary>
    public bool IsCompleteFailure => Error is not null && FailedChanges.Length == 0;

    private WriteResult(ReadOnlyMemory<SubjectPropertyChange> failedChanges, Exception? error)
    {
        FailedChanges = failedChanges;
        Error = error;
    }

    /// <summary>
    /// Gets a successful result where all changes were written (zero allocation).
    /// </summary>
    public static WriteResult Success { get; } = new(ReadOnlyMemory<SubjectPropertyChange>.Empty, null);

    /// <summary>
    /// Creates a failure result where all changes failed.
    /// Use when failure occurred before any individual writes (e.g., connection error).
    /// </summary>
    public static WriteResult Failure(Exception error) =>
        new(ReadOnlyMemory<SubjectPropertyChange>.Empty, error);

    /// <summary>
    /// Creates a failure result with the list of failed changes.
    /// Use when all provided changes failed and you want to track them.
    /// </summary>
    public static WriteResult Failure(ReadOnlyMemory<SubjectPropertyChange> failedChanges, Exception error) =>
        new(failedChanges, error);

    /// <summary>
    /// Creates a partial failure result with the specific changes that failed.
    /// </summary>
    /// <param name="failedChanges">The changes that failed to write.</param>
    /// <param name="error">The error that occurred.</param>
    public static WriteResult PartialFailure(ReadOnlyMemory<SubjectPropertyChange> failedChanges, Exception error) =>
        new(failedChanges, error);

    /// <summary>
    /// Creates a partial failure result where the last N changes failed sequentially.
    /// Use when writes are sequential and failure stops further processing.
    /// </summary>
    /// <param name="allChanges">The original array of all changes.</param>
    /// <param name="successCount">Number of changes that succeeded (from the start).</param>
    /// <param name="error">The error that caused the remaining changes to fail.</param>
    public static WriteResult PartialFailure(ReadOnlyMemory<SubjectPropertyChange> allChanges, int successCount, Exception error) =>
        new(allChanges.Slice(successCount), error);

    /// <summary>
    /// Creates a partial failure result by extracting failed changes using index mapping.
    /// Use when failures are sparse (non-sequential) in the original array.
    /// </summary>
    /// <param name="allChanges">The original array of all changes.</param>
    /// <param name="failedIndices">Array of indices that failed.</param>
    /// <param name="failedCount">Number of failed operations.</param>
    /// <param name="error">The error that occurred.</param>
    public static WriteResult PartialFailure(
        ReadOnlyMemory<SubjectPropertyChange> allChanges,
        int[] failedIndices,
        int failedCount,
        Exception error)
    {
        var failedChanges = new SubjectPropertyChange[failedCount];
        var span = allChanges.Span;
        for (var i = 0; i < failedCount; i++)
        {
            failedChanges[i] = span[failedIndices[i]];
        }
        return new(failedChanges, error);
    }
}

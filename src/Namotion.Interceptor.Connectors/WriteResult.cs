using System.Collections.Immutable;
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
    public ImmutableArray<SubjectPropertyChange> FailedChanges { get; }

    /// <summary>
    /// Gets the error that occurred during the write operation, or null if all changes succeeded.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>
    /// Gets a value indicating whether all attempted changes were successful.
    /// </summary>
    public bool IsFullySuccessful => Error is null;

    /// <summary>
    /// Gets a value indicating whether some changes succeeded while others failed.
    /// </summary>
    public bool IsPartialFailure { get; }

    private WriteResult(ImmutableArray<SubjectPropertyChange> failedChanges, Exception? error, bool isPartialFailure)
    {
        FailedChanges = failedChanges;
        Error = error;
        IsPartialFailure = isPartialFailure;
    }

    /// <summary>
    /// Gets a successful result where all changes were written (zero allocation).
    /// </summary>
    public static WriteResult Success { get; } = new([], null, false);

    /// <summary>
    /// Creates a failure result where all provided changes failed.
    /// </summary>
    /// <param name="failedChanges">The changes that failed to write.</param>
    /// <param name="error">The error that occurred.</param>
    public static WriteResult Failure(ReadOnlyMemory<SubjectPropertyChange> failedChanges, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new([..failedChanges.Span], error, isPartialFailure: false);
    }

    /// <summary>
    /// Creates a partial failure result where some changes succeeded and some failed.
    /// </summary>
    /// <param name="failedChanges">The changes that failed to write.</param>
    /// <param name="error">The error that occurred.</param>
    public static WriteResult PartialFailure(ReadOnlyMemory<SubjectPropertyChange> failedChanges, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new([..failedChanges.Span], error, isPartialFailure: true);
    }
}

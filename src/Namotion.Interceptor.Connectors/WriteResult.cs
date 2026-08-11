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
    /// Gets the changes not confirmed written to the external source; empty on full success, and
    /// unlisted changes count as written. Failed means unconfirmed, not rejected: consumers may retry
    /// a failed change but never revert it.
    /// <para>
    /// This is the source's answer about named changes, so enumerate the ones it refused. Leave the
    /// list empty, by way of <see cref="CallFailed"/>, only when the call itself failed and there is no
    /// per-change answer. <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/> expands the
    /// empty list to the batch's own changes, so what a caller sees reported failed is the same either way.
    /// </para>
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
    /// Creates a result for a call that failed without answering about any single change, such as a
    /// timeout, a dropped connection or a source that is not running. The whole attempted batch counts
    /// as failed, and naming no change additionally tells
    /// <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/> to stop rather than spend another
    /// transport timeout on each remaining batch of the same flush. Use <see cref="Failure"/> instead
    /// whenever the source did answer about these changes and refused them.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    public static WriteResult CallFailed(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new([], error, isPartialFailure: false);
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

    /// <summary>
    /// Creates a partial failure result taking ownership of <paramref name="failedChanges"/> without
    /// copying. The caller must not retain or mutate the underlying array.
    /// </summary>
    /// <param name="failedChanges">The changes that failed to write.</param>
    /// <param name="error">The error that occurred.</param>
    internal static WriteResult PartialFailure(ImmutableArray<SubjectPropertyChange> failedChanges, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(failedChanges, error, isPartialFailure: true);
    }
}

using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a source that synchronizes data FROM an external system to a subject.
/// The external system is the source of truth; the C# object is a replica.
/// Sources must claim ownership of properties by calling <c>SetSource(this)</c> during initialization.
/// </summary>
public interface ISubjectSource : ISubjectConnector
{
    /// <summary>
    /// Gets the maximum number of property changes that can be applied in a single batch (0 = no limit).
    /// </summary>
    public int WriteBatchSize { get; }

    /// <summary>
    /// Applies a set of property changes to the source.
    /// Returns a <see cref="WriteResult"/> indicating which changes succeeded.
    /// On partial failure, returns the subset of changes that were successfully written.
    /// </summary>
    /// <remarks>
    /// Thread-safety is handled automatically by <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/>,
    /// which should be used by all callers instead of this method directly.
    /// Implement <see cref="ISupportsConcurrentWrites"/> to opt-out of automatic synchronization.
    /// Do not retain <paramref name="changes"/> after the returned task completes: the caller may
    /// reuse or mutate the underlying buffer.
    /// When reporting an error, enumerate the failed changes; see <see cref="WriteResult.FailedChanges"/>.
    /// </remarks>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="WriteResult"/> containing successful changes and any error.</returns>
    ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the initial state from the external authoritative system and returns a delegate that applies it to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the initial state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the source's synchronization state. Describes the inbound direction only: the model
    /// mirroring the external system. Outbound backlog is <see cref="PendingWriteCount"/>.
    /// </summary>
    SourceState State { get; }

    /// <summary>
    /// Gets when the most recent initial synchronization completed, or <c>null</c> if it never has.
    /// While <see cref="SourceState.Connecting"/> after a drop, this is how a dashboard says
    /// "stale, last confirmed at T".
    /// </summary>
    DateTimeOffset? LastSynchronizedAt { get; }

    /// <summary>
    /// Gets the number of writes currently queued for retry. Orthogonal to <see cref="State"/>:
    /// this queue can be non-empty during entirely normal synchronized operation.
    /// </summary>
    int PendingWriteCount { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    /// <remarks>
    /// Raised synchronously on the transitioning thread and inside the source's transition lock.
    /// Handlers MUST be observe-only: they must not block, and must not cause a transition of any
    /// source, directly or indirectly, because the lock is reentrant and a nested transition would
    /// publish out of order. Mutating consumers belong on the SourceMonitor stream, where delivery
    /// is queued and outside all locks.
    /// </remarks>
    event EventHandler<SourceEvent>? StateChanged;
}

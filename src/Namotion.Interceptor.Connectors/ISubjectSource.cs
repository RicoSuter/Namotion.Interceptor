using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a source that synchronizes data FROM an external system to a subject.
/// The external system is the source of truth; the C# object is a replica.
/// Sources must claim ownership of properties by calling <c>SetSource(this)</c> during initialization.
/// </summary>
/// <remarks>
/// <see cref="ISubjectConnector.RootSubject"/>, <see cref="State"/> and <see cref="StateChangeTime"/>
/// must be lock-free. <c>SourceMonitor</c> reads them while holding its own lock, and
/// <see cref="StateChanged"/> is raised from inside the source's transition lock, so a getter that
/// took that lock would close an ABBA cycle. <see cref="SubjectSourceBase"/> satisfies this.
/// <para>
/// Implementing this directly instead of deriving from <see cref="SubjectSourceBase"/> means
/// registering with every monitor from <c>subject.Context.GetServices&lt;SourceMonitor&gt;()</c> on
/// start and unregistering on stop. Skipping that is silent rather than fatal: the source never
/// appears in the stream, and branch-scoped waits covering it complete vacuously.
/// </para>
/// </remarks>
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
    /// mirroring the external system. Outbound backlog is reported by the connector's diagnostics.
    /// </summary>
    SourceState State { get; }

    /// <summary>
    /// Gets when <see cref="State"/> last changed. Stamped at construction, so it is always
    /// meaningful. Read with <see cref="State"/> it answers both questions an operator asks:
    /// <c>Synchronized</c> plus T reads as in sync since T, <c>Synchronizing</c> plus T reads as
    /// stale since T.
    /// </summary>
    DateTimeOffset StateChangeTime { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    /// <remarks>
    /// Raised synchronously on the transitioning thread, inside the source's transition lock, so
    /// handlers must be observe-only: no blocking, and no causing a transition of any source. The
    /// lock is reentrant, so a nested transition would publish out of order. Mutating consumers
    /// belong on the SourceMonitor stream, where delivery is queued outside all locks.
    /// </remarks>
    event EventHandler<SourceEvent>? StateChanged;
}

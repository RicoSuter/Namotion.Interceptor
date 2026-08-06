using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a source that synchronizes data FROM an external system to a subject.
/// The external system is the source of truth; the C# object is a replica.
/// Sources must claim ownership of properties by calling <c>SetSource(this)</c> during initialization.
/// </summary>
/// <remarks>
/// <see cref="ISubjectConnector.RootSubject"/>, <see cref="State"/>, and <see cref="LastSynchronizedAt"/> must not
/// acquire any lock that is held while <see cref="StateChanged"/> is raised. A registered source is
/// read through these getters by <c>SourceMonitor</c>, which itself holds its own lock while doing
/// so (building the SourceRegistered/SourceUnregistered event, and evaluating a branch-scoped wait).
/// If a getter here acquired the source's own transition lock, a StateChanged raise on one thread
/// (holding the transition lock, waiting to enter SourceMonitor's lock via the event handler) could
/// deadlock against a SourceMonitor caller on another thread (holding SourceMonitor's lock, waiting
/// to enter the transition lock via one of these getters) - a classic ABBA lock-order inversion.
/// <see cref="SubjectSourceBase"/> is safe because these getters are lock-free (Volatile.Read /
/// Interlocked.Read / a stored reference); a custom implementer overriding them, or wrapping a
/// property with its own synchronization, must preserve that.
/// <para>
/// A source implementing this interface directly, rather than deriving from
/// <see cref="SubjectSourceBase"/>, is otherwise invisible to source monitoring: it must register
/// itself with every monitor reachable from the subject's context, calling
/// <see cref="SourceMonitor.Register(ISubjectSource)"/> when it starts and
/// <see cref="SourceMonitor.Unregister(ISubjectSource)"/> when it stops or is disposed, once for
/// each monitor returned by looping <c>subject.Context.GetServices&lt;SourceMonitor&gt;()</c>. Use
/// that general service lookup, not <see cref="SourceMonitoringExtensions.GetSourceMonitor"/>: the
/// singular convenience method throws when no monitor is reachable, which would crash a direct
/// implementer calling it in any application that has not called <c>WithSourceMonitoring()</c>.
/// Skipping registration is silent, not a hang: the source never appears in
/// <see cref="SourceMonitor.Sources"/>, no <see cref="SourceEventKind.SourceRegistered"/> or
/// <see cref="SourceEventKind.SourceUnregistered"/> event is ever published for it, and once
/// registration is complete, any branch-scoped wait whose scope depends on it completes
/// vacuously instead of blocking, since the wait engine cannot distinguish "no source registered
/// for this branch" from "no source for this branch, ever". <see cref="SubjectSourceBase"/>
/// performs this registration automatically around its pump lifecycle.
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
    /// Raised synchronously on the transitioning thread, inside the source's transition lock.
    /// Handlers must be observe-only: no blocking, and no causing a transition of any source
    /// (directly or indirectly), since the lock is reentrant and a nested transition would publish
    /// out of order. Mutating consumers belong on the SourceMonitor stream, where delivery is
    /// queued outside all locks.
    /// <para>
    /// A stub implementation that declares this event but does not yet raise it must still give it
    /// an explicit body, <c>{ add { } remove { } }</c>, rather than a plain auto-event: an event
    /// that is never raised is otherwise flagged as unused under warnings-as-errors (CS0067).
    /// </para>
    /// </remarks>
    event EventHandler<SourceEvent>? StateChanged;
}

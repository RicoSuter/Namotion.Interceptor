using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Writes inbound property updates from sources to subjects.
/// Implements the buffer-load-replay pattern to ensure eventual consistency during source initialization.
/// </summary>
/// <remarks>
/// During initialization, updates are buffered. Once <see cref="LoadInitialStateAndResumeAsync"/> is called,
/// the initial state is loaded, buffered updates are replayed, and subsequent writes are applied immediately.
/// This buffering behavior is transparent to sources - they simply call <see cref="Write{TState}"/>.
/// </remarks>
public sealed class SubjectPropertyWriter
{
    private readonly SubjectSourceBase _source;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    private List<Action>? _updates = [];

    // Bumped by every StartBuffering call. LoadInitialStateAndResumeAsync captures the generation
    // in effect when it starts and compares it again after its (possibly long) await: if a later
    // StartBuffering happened in between, this call's snapshot is stale and must not be applied,
    // replayed, or certified as Synchronized - see LoadInitialStateAndResumeAsync.
    private int _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectPropertyWriter"/> class.
    /// </summary>
    /// <param name="source">The source associated with this writer.</param>
    /// <param name="logger">The logger.</param>
    /// <remarks>
    /// Typed to the concrete base rather than <see cref="ISubjectSource"/> because this writer
    /// drives the source's state transitions, which only <see cref="SubjectSourceBase"/> defines. A
    /// source implementing the interface directly owns its own write path and its own transitions.
    /// </remarks>
    public SubjectPropertyWriter(SubjectSourceBase source, ILogger logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <summary>
    /// Starts buffering updates instead of applying them directly.
    /// Buffered updates will be replayed when <see cref="LoadInitialStateAndResumeAsync"/> is called.
    /// This method should be called before the source starts listening for changes.
    /// </summary>
    public void StartBuffering()
    {
        lock (_lock)
        {
            _updates = [];
            _generation++;

            // Buffering starts exactly when the source has stopped trusting its live feed, on first
            // connect and on every reconnect, including reconnects the base pump never sees. Reported
            // while still holding _lock, symmetric with LoadInitialStateAndResumeAsync's own
            // TransitionTo(Synchronized) call below: both transitions are paired with the generation
            // change that governs them so neither can be observed out of sync with it.
            _source.TransitionTo(SourceState.Connecting);
        }
    }

    /// <summary>
    /// Invalidates the current generation without touching the update buffer, for a connection loss
    /// detected before the reconnect's own <see cref="StartBuffering"/> call runs.
    /// </summary>
    /// <remarks>
    /// A <see cref="LoadInitialStateAndResumeAsync"/> call already in flight when the connection
    /// drops captured the OLD generation before it started awaiting; without this call it has no way
    /// to learn the connection went away mid-load; it would apply pre-outage data and report
    /// Synchronized once its await returns, and that false state would persist until the next
    /// reconnect cycle's own StartBuffering. Bumping the generation here makes that in-flight call's
    /// own check see itself as superseded, so it discards instead. Deliberately does not reset
    /// _updates the way StartBuffering does: replacing the buffer here would discard whatever has
    /// been collected since the outage was detected, and the reconnect's own StartBuffering, which
    /// runs later, is what actually needs a fresh buffer to start from.
    /// </remarks>
    internal void InvalidateGeneration()
    {
        lock (_lock)
        {
            _generation++;
        }
    }

    /// <summary>
    /// Completes initialization by loading initial state from the source
    /// and replaying all buffered updates. This ensures zero data loss during the initialization period.
    /// </summary>
    /// <remarks>
    /// If the load fails, the exception propagates to signal initialization failure
    /// and trigger reconnection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task LoadInitialStateAndResumeAsync(CancellationToken cancellationToken)
    {
        // Published under _lock by StartBuffering/InvalidateGeneration; the comparison that matters
        // happens under the lock below, so this read needs no lock of its own.
        var generation = Volatile.Read(ref _generation);

        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (generation != _generation)
            {
                // A later StartBuffering happened while this call was awaiting LoadInitialStateAsync,
                // so the snapshot just returned is stale: applying it now would overwrite whatever
                // the newer cycle has already written (or will write), and reporting Synchronized
                // would certify that stale data as current. Discard the apply action entirely, don't
                // touch _updates (the newer cycle owns that buffer), and skip the report below.
                _logger.LogDebug("LoadInitialStateAndResumeAsync discarded a stale snapshot superseded by a later reconnect.");
                return;
            }

            applyAction?.Invoke();

            // Replay previously buffered updates
            var updates = _updates;
            if (updates is null)
            {
                // Already replayed by a concurrent/previous call (race between automatic and manual reconnection).
                // This is safe - it means another reconnection cycle already loaded state and replayed updates.
                _logger.LogDebug("LoadInitialStateAndResumeAsync called but updates already replayed by concurrent reconnection.");
            }
            else
            {
                foreach (var action in updates)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to apply subject update.");
                    }
                }

                // Must be after replay: Write() reads _updates without lock on the fast path.
                _updates = null;
            }

            // Reported while still holding _lock, atomically with the generation check above, so a
            // StartBuffering landing in between cannot let a superseded cycle certify Synchronized.
            // Lock order writer._lock -> _stateLock -> monitor._lock (TransitionTo can reach a
            // registered monitor synchronously) is never reversed anywhere, so it cannot deadlock.
            _source.TransitionTo(SourceState.Synchronized);
        }
    }

    /// <summary>
    /// Writes a property update to the subject. During initialization, the update is buffered;
    /// otherwise it is applied immediately. This buffering is transparent to the caller.
    /// </summary>
    /// <param name="state">The state provided to the action (allows static delegates to avoid allocations).</param>
    /// <param name="update">The update action to apply to the subject.</param>
    public void Write<TState>(TState state, Action<TState> update)
    {
        // Hot path optimization: plain read (no volatile read) is fastest.
        // Changes to _updates are rare (only during initialization/reconnection).
        // If we see stale non-null during transition, we take lock and re-check - still correct.
        var updates = _updates;
        if (updates is not null)
        {
            lock (_lock)
            {
                updates = _updates;
                if (updates is not null)
                {
                    // Still initializing, buffer the update (cold path, allocations acceptable)
                    AddBeforeInitializationUpdate(updates, state, update);
                    return;
                }
            }
        }

        try
        {
            update(state);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to apply subject update.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddBeforeInitializationUpdate<TState>(List<Action> beforeInitializationUpdates, TState state, Action<TState> update)
    {
        // The allocation for the closure happens only on the cold path (needs to be in an own non-inlined method
        // to avoid capturing unnecessary locals and causing allocations on the hot path).
        beforeInitializationUpdates.Add(() => update(state));
    }
}

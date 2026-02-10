using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Writes inbound property updates from sources to subjects.
/// Implements the buffer-load-replay pattern to ensure eventual consistency during source initialization.
/// </summary>
/// <remarks>
/// During initialization, updates are buffered. Once <see cref="CompleteInitializationWithInitialStateAsync"/> or
/// <see cref="CompleteInitialization"/> is called, buffered updates are replayed and
/// subsequent writes are applied immediately.
/// This buffering behavior is transparent to sources - they simply call <see cref="Write{TState}"/>.
/// </remarks>
public sealed class SubjectPropertyWriter
{
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, ValueTask<bool>>? _flushRetryQueueAsync;
    private readonly Lock _lock = new();

    private List<Action>? _updates = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectPropertyWriter"/> class.
    /// </summary>
    /// <param name="source">The source associated with this writer.</param>
    /// <param name="flushRetryQueueAsync">Optional callback to flush pending outbound writes from the retry queue.</param>
    /// <param name="logger">The logger.</param>
    public SubjectPropertyWriter(ISubjectSource source, Func<CancellationToken, ValueTask<bool>>? flushRetryQueueAsync, ILogger logger)
    {
        _source = source;
        _logger = logger;
        _flushRetryQueueAsync = flushRetryQueueAsync;
    }

    /// <summary>
    /// Starts buffering updates instead of applying them directly.
    /// Buffered updates will be replayed when <see cref="CompleteInitializationWithInitialStateAsync"/> or
    /// <see cref="CompleteInitialization"/> is called.
    /// This method should be called before the source starts listening for changes.
    /// </summary>
    public void StartBuffering()
    {
        lock (_lock)
        {
            _updates = [];
        }
    }

    /// <summary>
    /// Completes initialization by flushing pending writes, loading initial state from the source,
    /// and replaying all buffered updates. This ensures zero data loss during the initialization period.
    /// </summary>
    /// <remarks>
    /// The flush happens first so that the server has the latest local changes before we load state,
    /// avoiding visible state toggles where the UI briefly shows old server values.
    /// If the flush or load fails, the exception propagates to signal initialization failure
    /// and trigger reconnection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task CompleteInitializationWithInitialStateAsync(CancellationToken cancellationToken)
    {
        // Flush pending writes first (so server has latest before we load state)
        if (_flushRetryQueueAsync is not null)
        {
            await _flushRetryQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);
        CompleteInitialization(applyAction);
    }

    /// <summary>
    /// Completes initialization by replaying buffered updates and resuming direct writes,
    /// without flushing pending writes or loading state from the source.
    /// Use after a preserved session reconnect where subscriptions are maintained and data is still flowing.
    /// For full initialization (flush + load + replay), use <see cref="CompleteInitializationWithInitialStateAsync"/> instead.
    /// </summary>
    /// <param name="applyBeforeReplay">Optional action to apply before replaying buffered updates (e.g. loaded initial state).</param>
    public void CompleteInitialization(Action? applyBeforeReplay = null)
    {
        lock (_lock)
        {
            applyBeforeReplay?.Invoke();

            var updates = _updates;
            if (updates is null)
            {
                _logger.LogDebug("CompleteInitialization called but updates already replayed by concurrent reconnection.");
                return;
            }

            _updates = null;
            foreach (var action in updates)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to apply buffered update.");
                }
            }
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

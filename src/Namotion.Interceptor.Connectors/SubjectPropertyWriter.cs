using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

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
    /// Buffered updates will be replayed when <see cref="LoadInitialStateAndResumeAsync"/> is called.
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
    /// Replays all buffered updates and resumes immediate write mode.
    /// Use this after SDK reconnection with subscription transfer, where the subscription
    /// mechanism handles state synchronization and a full state read is not needed.
    /// </summary>
    public void ReplayBufferAndResume()
    {
        lock (_lock)
        {
            ReplayBufferAndResumeWithoutLock();
        }
    }

    private void ReplayBufferAndResumeWithoutLock()
    {
        var updates = _updates;
        _updates = null;

        if (updates is not null)
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
    public async Task LoadInitialStateAndResumeAsync(CancellationToken cancellationToken)
    {
        // Flush pending writes first (so server has latest before we load state)
        if (_flushRetryQueueAsync is not null)
        {
            await _flushRetryQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            applyAction?.Invoke();
            ReplayBufferAndResumeWithoutLock();
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

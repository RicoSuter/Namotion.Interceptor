using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Writes inbound property updates from sources to subjects.
/// Implements the buffer-load-replay pattern to ensure eventual consistency during source initialization.
/// </summary>
/// <remarks>
/// During initialization, updates are buffered. Once <see cref="CompleteInitializationAsync"/> is called,
/// the initial state is loaded, buffered updates are replayed, and subsequent writes are applied immediately.
/// This buffering behavior is transparent to sources - they simply call <see cref="Write{TState}"/>.
/// </remarks>
public sealed class SubjectPropertyWriter
{
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, ValueTask<bool>>? _onInitializationCompleted;
    private readonly Lock _lock = new();

    private List<Action>? _updates = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectPropertyWriter"/> class.
    /// </summary>
    /// <param name="source">The source associated with this writer.</param>
    /// <param name="onInitializationCompleted">Optional callback invoked after initialization completes (e.g., to flush pending outbound writes).</param>
    /// <param name="logger">The logger.</param>
    public SubjectPropertyWriter(ISubjectSource source, Func<CancellationToken, ValueTask<bool>>? onInitializationCompleted, ILogger logger)
    {
        _source = source;
        _logger = logger;
        _onInitializationCompleted = onInitializationCompleted;
    }

    /// <summary>
    /// Starts buffering updates instead of applying them directly.
    /// Buffered updates will be replayed when <see cref="CompleteInitializationAsync"/> is called.
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
    /// Completes initialization by loading initial state from the source, replaying all buffered updates,
    /// and invoking the initialization completed callback.
    /// This ensures zero data loss during the initialization period.
    /// </summary>
    /// <remarks>
    /// If the callback fails, the exception propagates to signal initialization failure
    /// and trigger reconnection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task CompleteInitializationAsync(CancellationToken cancellationToken)
    {
        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            applyAction?.Invoke();

            // Replay previously buffered updates
            var updates = _updates;
            if (updates is null)
            {
                // Already replayed by a concurrent/previous call (race between automatic and manual reconnection).
                // This is safe - it means another reconnection cycle already loaded state and replayed updates.
                _logger.LogDebug("CompleteInitializationAsync called but updates already replayed by concurrent reconnection.");
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
                    _logger.LogError(e, "Failed to apply subject update.");
                }
            }
        }

        // Invoke callback after initialization/reconnection completes
        if (_onInitializationCompleted is not null)
        {
            await _onInitializationCompleted(cancellationToken).ConfigureAwait(false);
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

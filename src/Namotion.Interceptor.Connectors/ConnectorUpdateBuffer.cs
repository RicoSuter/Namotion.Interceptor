using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Buffers updates from a connector during initialization and replays them after loading complete state.
/// Implements the queue-read-replay pattern to ensure zero data loss during connector initialization.
/// </summary>
public sealed class ConnectorUpdateBuffer
{
    private readonly ISubjectConnector _connector;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, ValueTask<bool>>? _flushRetryQueue;
    private readonly Lock _lock = new();

    private List<Action>? _updates = [];

    public ConnectorUpdateBuffer(ISubjectConnector connector, Func<CancellationToken, ValueTask<bool>>? flushRetryQueue, ILogger logger)
    {
        _connector = connector;
        _logger = logger;
        _flushRetryQueue = flushRetryQueue;
    }

    /// <summary>
    /// Starts buffering updates instead of applying them directly.
    /// Buffered updates will be replayed when <see cref="CompleteInitializationAsync"/> is called.
    /// This method should be called before the connector starts listening for changes.
    /// </summary>
    public void StartBuffering()
    {
        lock (_lock)
        {
            _updates = [];
        }
    }

    /// <summary>
    /// Completes initialization by loading complete state from the connector, replaying all buffered updates,
    /// and flushing any queued writes that accumulated during disconnection.
    /// This ensures zero data loss during the initialization period.
    /// </summary>
    /// <remarks>
    /// If the retry queue flush fails, the exception propagates to signal initialization failure
    /// and trigger reconnection. Queued writes remain in the queue for retry on next connection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task CompleteInitializationAsync(CancellationToken cancellationToken)
    {
        Action? applyAction = null;

        if (_connector is ISubjectClientConnector clientConnector)
        {
            applyAction = await clientConnector.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);
        }

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

        // Flush retry queue after initialization/reconnection
        if (_flushRetryQueue is not null)
        {
            await _flushRetryQueue(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies an update by either buffering it during initialization or executing it immediately.
    /// The buffering behavior is transparent to the caller - updates are always eventually applied.
    /// </summary>
    /// <param name="state">The state provided to the action (avoid delegate allocations by allowing action to be static).</param>
    /// <param name="update">The update action to apply.</param>
    public void ApplyUpdate<TState>(TState state, Action<TState> update)
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

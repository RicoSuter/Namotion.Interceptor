using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Sources;

public class SubjectUpdater : ISubjectUpdater
{
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private List<Action>? _updates = [];

    public SubjectUpdater(ISubjectSource source, ILogger logger)
    {
        _source = source;
        _logger = logger;
    }
    
    public void StartCollectingUpdates()
    {
        lock (_lock)
        {
            _updates = [];
        }
    }
    
    public async Task LoadCompleteStateAndReplayUpdatesAsync(CancellationToken cancellationToken)
    {
        var applyAction = await _source.LoadCompleteSourceStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            applyAction?.Invoke();

            // Replay previously buffered updates
            var updates = _updates;
            if (updates is null)
            {
                // Already replayed by a concurrent/previous call (race between automatic and manual reconnection).
                // This is safe - it means another reconnection cycle already loaded state and replayed updates.
                _logger.LogDebug("LoadCompleteStateAndReplayUpdatesAsync called but updates already replayed by concurrent reconnection.");
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
    }
    
    /// <inheritdoc />
    public void EnqueueOrApplyUpdate<TState>(TState state, Action<TState> update)
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
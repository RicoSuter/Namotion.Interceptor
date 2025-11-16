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
        var beforeInitializationUpdates = _updates;
        if (beforeInitializationUpdates is not null)
        {
            lock (_lock)
            {
                beforeInitializationUpdates = _updates;
                if (beforeInitializationUpdates is not null)
                {
                    // Accepted closure memory allocation: Lambda captures 'update' and 'state'
                    // This is acceptable because buffering only occurs during startup/reconnection (cold path).
                    beforeInitializationUpdates.Add(() => update(state));
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
}
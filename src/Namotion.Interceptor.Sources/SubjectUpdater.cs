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
        lock (_lock)
        {
            _updates = [];
        }
        
        var applyAction = await _source.LoadCompleteSourceStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            applyAction?.Invoke();

            // Replay previously buffered updates
            var beforeInitializationUpdates = _updates;
            _updates = null;

            foreach (var action in beforeInitializationUpdates!)
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
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

public partial class Game
{
    // Touched only on the simulation thread, and by StopAsync once the simulation stopped.
    private long _lastSaveTimestamp;
    private bool _isSaving;
    private Task<bool> _saveTask = Task.FromResult(true);

    /// <summary>
    /// The running or last save, true when it wrote or nothing can write; read it on the thread that runs the ticks.
    /// </summary>
    internal Task<bool> SaveTask => _saveTask;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is { IsCompleted: false })
        {
            _logger.LogWarning("The game {Game} stopped before it was saved and loses its latest changes on restart", Title);
            return;
        }

        // A game whose loop never ran stops its simulation here; afterwards this thread is the only one on the state.
        _simulation.Stop();
        try
        {
            var isLastSaveWritten = await _saveTask.WaitAsync(cancellationToken);
            var isSaveRequested = !isLastSaveWritten;
            _simulation.RunExclusive(() =>
            {
                _publisher.Publish();
                isSaveRequested |= _world.IsSaveRequested;
                _world.IsSaveRequested = false;
            });

            if (isSaveRequested && !await WriteConfigurationAsync(cancellationToken))
            {
                _logger.LogWarning("The game {Game} stopped before it was saved and loses its latest changes on restart", Title);
            }
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "The game {Game} stopped before it was saved and loses its latest changes on restart", Title);
        }
    }

    // Starts a save at most every SaveInterval when something changed: publishes first, then pauses publishing until the
    // write finished, so the file holds one consistent publish.
    private void StartSaveWhenDue()
    {
        var now = _timeProvider.GetTimestamp();
        if (_isSaving || !_world.IsSaveRequested || _timeProvider.GetElapsedTime(_lastSaveTimestamp, now) < GameSimulationSettings.SaveInterval)
        {
            return;
        }

        if (_publisher.Publish())
        {
            WorldChanged?.Invoke();
        }

        _world.IsSaveRequested = false;
        _isSaving = true;
        _publisher.IsPaused = true;
        _lastSaveTimestamp = now;
        _saveTask = SaveAsync();
    }

    private async Task<bool> SaveAsync()
    {
        var isWritten = false;
        try
        {
            // Off the simulation thread, because the storage serializes the whole game before it awaits anything.
            isWritten = await Task.Run(() => WriteConfigurationAsync(CancellationToken.None)).ConfigureAwait(false);
            return isWritten;
        }
        finally
        {
            // Dropped once the simulation stopped; StopAsync then reads the result of this task.
            _simulation.Post(() => CompleteSave(isWritten));
        }
    }

    private void CompleteSave(bool isWritten)
    {
        _isSaving = false;
        _publisher.IsPaused = false;
        if (!isWritten)
        {
            _world.IsSaveRequested = true;
        }
    }

    /// <summary>
    /// Writes the configuration through the storage that holds the game; logs instead of throwing when that fails.
    /// </summary>
    /// <returns>False when a storage failed to write, so that writing again may succeed.</returns>
    private async Task<bool> WriteConfigurationAsync(CancellationToken cancellationToken)
    {
        var writer = this.TryGetFirstParent<IConfigurationWriter>();
        if (writer is null)
        {
            // No storage holds the game, which writing again does not change.
            _logger.LogWarning("The game {Game} was not saved and loses its changes on restart", Title);
            return true;
        }

        try
        {
            if (await writer.WriteConfigurationAsync(this, cancellationToken))
            {
                return true;
            }

            _logger.LogWarning("The game {Game} was not saved and is saved again later", Title);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Saving the game {Game} failed and is tried again later", Title);
        }

        return false;
    }
}

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Registers, manages and dispatches mutation actions on subjects.
/// Used to delay, batch, sequence, or replay updates to subjects.
/// </summary>
public interface ISubjectUpdater
{
    /// <summary>
    /// Starts to collect updates instead of applying them directly to be replayed after complete state has been loaded.
    /// This method should be called be
    /// </summary>
    void StartCollectingUpdates();

    /// <summary>
    /// Loads the complete state and replays all collected updates.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    Task LoadCompleteStateAndReplayUpdatesAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Enqueues (during startup) or directly applies an update to be applied (might update multiple properties).
    /// </summary>
    /// <param name="state">The state provided to the action (avoid delegate allocations by allowing action to be static).</param>
    /// <param name="update">The update action.</param>
    void EnqueueOrApplyUpdate<TState>(TState state, Action<TState> update);
}
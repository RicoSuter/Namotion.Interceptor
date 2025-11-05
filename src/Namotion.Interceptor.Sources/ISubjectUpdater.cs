namespace Namotion.Interceptor.Sources;

/// <summary>
/// Registers, manages and dispatches mutation actions on subjects.
/// Used to delay, batch, sequence, or replay updates to subjects.
/// </summary>
public interface ISubjectUpdater
{
    /// <summary>
    /// Enqueues (during startup) or directly applies an update to be applied (might update multiple properties).
    /// </summary>
    /// <param name="state">The state provided to the action (avoid delegate allocations by allowing action to be static).</param>
    /// <param name="update">The update action.</param>
    void EnqueueOrApplyUpdate<TState>(TState state, Action<TState> update);
}
namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Coordinates mutate/converge cycle phases. All engines call WaitIfPaused()
/// in their loops so the verification engine can pause everything for convergence checks.
/// </summary>
public class TestCycleCoordinator
{
    private readonly ManualResetEventSlim _runSignal = new(true);
    private int _currentCycle;

    public int CurrentCycle => Volatile.Read(ref _currentCycle);

    /// <summary>Pause all engines (enter converge phase).</summary>
    public void Pause() => _runSignal.Reset();

    /// <summary>Resume all engines (enter mutate phase).</summary>
    public void Resume() => _runSignal.Set();

    public void SetCycle(int cycle) => Volatile.Write(ref _currentCycle, cycle);

    /// <summary>
    /// Blocks the calling thread if currently paused; returns immediately if running.
    /// Returns <c>true</c> if a pause was actually waited on, <c>false</c> if the call
    /// returned without blocking. Callers with wall-clock rate limits whose reference
    /// timestamps go stale during the pause can use this to resync their state.
    /// </summary>
    public bool WaitIfPaused(CancellationToken cancellationToken)
    {
        if (_runSignal.IsSet)
        {
            return false;
        }
        _runSignal.Wait(cancellationToken);
        return true;
    }
}

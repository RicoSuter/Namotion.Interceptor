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

    /// <summary>Blocks the calling thread if currently paused. Returns immediately if running.</summary>
    public void WaitIfPaused(CancellationToken cancellationToken) => _runSignal.Wait(cancellationToken);
}

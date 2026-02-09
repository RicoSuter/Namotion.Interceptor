namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Coordinates mutate/converge cycle phases. All engines call WaitIfPaused()
/// in their loops so the verification engine can pause everything for convergence checks.
/// </summary>
public class TestCycleCoordinator
{
    private readonly ManualResetEventSlim _runSignal = new(true);

    /// <summary>Pause all engines (enter converge phase).</summary>
    public void Pause() => _runSignal.Reset();

    /// <summary>Resume all engines (enter mutate phase).</summary>
    public void Resume() => _runSignal.Set();

    /// <summary>Blocks the calling thread if currently paused. Returns immediately if running.</summary>
    public void WaitIfPaused(CancellationToken cancellationToken) => _runSignal.Wait(cancellationToken);
}

using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A <see cref="BackgroundService"/> whose execution is the delegate the test hands it, so a run that
/// throws, parks or signals is scripted at the call site rather than in one model per shape.
/// </summary>
public sealed class ScriptedBackgroundService : BackgroundService
{
    private readonly Func<CancellationToken, Task> _run;
    private int _stopCount;
    private int _disposeCount;

    public ScriptedBackgroundService(Func<CancellationToken, Task> run)
    {
        _run = run;
    }

    public int StopCount => Volatile.Read(ref _stopCount);

    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _run(stoppingToken);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        base.Dispose();
    }
}

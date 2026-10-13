using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is its own <see cref="BackgroundService"/>, with each run scripted by the test.
/// The script is read once per run, at entry, so a test can rescript the subject between the runs a
/// restart in place gives it.
/// </summary>
[InterceptorSubject]
public partial class ScriptedHostedSubject : BackgroundService
{
    private Func<CancellationToken, Task>? _run;
    private int _stopCount;

    public partial string? Name { get; set; }

    /// <summary>The execution of the next run. Null parks the run until its token is cancelled.</summary>
    public Func<CancellationToken, Task>? Run
    {
        get => Volatile.Read(ref _run);
        set => Volatile.Write(ref _run, value);
    }

    public int StopCount => Volatile.Read(ref _stopCount);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Run is { } run ? run(stoppingToken) : Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        return base.StopAsync(cancellationToken);
    }
}

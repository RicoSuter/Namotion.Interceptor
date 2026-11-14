using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is its own hosted service and whose start parks until the test releases it, so a
/// test can cancel a wait for that start while it is still running.
/// </summary>
[InterceptorSubject]
public partial class ParkedStartHostedSubject : IHostedService
{
    private int _stopCount;

    public partial string? Name { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StopCount => Volatile.Read(ref _stopCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        return Release.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        return Task.CompletedTask;
    }
}

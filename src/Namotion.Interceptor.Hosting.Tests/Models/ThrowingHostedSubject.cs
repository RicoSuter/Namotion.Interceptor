using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject whose own start always fails, so a caller waiting for that start has a fault to observe.
/// </summary>
[InterceptorSubject]
public partial class ThrowingHostedSubject : IHostedService
{
    private int _stopCount;

    public partial string? Name { get; set; }

    /// <summary>The handler never disposes a subject, so the stop is the only cleanup this one gets.</summary>
    public int StopCount => Volatile.Read(ref _stopCount);

    /// <summary>Awaited inside <see cref="StartAsync"/> before it fails, when set.</summary>
    public Func<Task>? StartHold { get; init; }

    public Task StartAsync(CancellationToken cancellationToken)
        => StartHold is { } hold ? FailAfterAsync(hold) : throw new InvalidOperationException("start failed");

    private static async Task FailAfterAsync(Func<Task> hold)
    {
        await hold();
        throw new InvalidOperationException("start failed");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        return Task.CompletedTask;
    }
}

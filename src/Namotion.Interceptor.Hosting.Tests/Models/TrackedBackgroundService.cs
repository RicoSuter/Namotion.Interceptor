using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

public sealed class TrackedBackgroundService : IHostedService, IAsyncDisposable
{
    private int _disposeCount;

    public bool ThrowOnStart { get; init; }

    public bool IsStarted { get; private set; }

    public bool IsStopped { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("start failed");
        }

        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Honours the token like any real hosted service, which is what makes a cancelled stop
        // observable: the handler still has to dispose an instance whose stop was cut short.
        cancellationToken.ThrowIfCancellationRequested();

        IsStopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

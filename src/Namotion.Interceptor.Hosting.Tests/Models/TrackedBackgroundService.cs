using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

public sealed class TrackedBackgroundService : IHostedService, IAsyncDisposable
{
    private int _disposeCount;
    private int _startCount;

    public bool ThrowOnStart { get; init; }

    /// <summary>
    /// Throws from <see cref="DisposeAsync"/>, which the handler runs inside a property write on the
    /// detach path, so an exception escaping it surfaces at an unrelated assignment.
    /// </summary>
    public bool ThrowOnDispose { get; init; }

    public bool IsStarted { get; private set; }

    /// <summary>Calls into <see cref="StartAsync"/>, so a start on a disposed instance is measurable.</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    public bool IsStopped { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);

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

        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("dispose failed");
        }

        return ValueTask.CompletedTask;
    }
}

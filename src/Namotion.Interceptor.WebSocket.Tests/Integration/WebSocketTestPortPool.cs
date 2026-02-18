using System;
using System.Threading;
using System.Threading.Tasks;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Manages a pool of ports for parallel WebSocket integration tests.
/// Uses round-robin port assignment to avoid port reuse within a test run,
/// preventing TIME_WAIT socket issues.
/// </summary>
public static class WebSocketTestPortPool
{
    private const int BasePort = 18100;
    private const int PoolSize = 100;
    private const int MaxParallel = 4;
    private const int AcquisitionTimeoutMs = 5 * 60 * 1000; // 5 minutes

    private static readonly SemaphoreSlim Semaphore = new(MaxParallel, MaxParallel);
    private static int _nextPortIndex = -1;

    /// <summary>
    /// Acquires a port from the pool using round-robin assignment.
    /// Limits concurrent tests to MaxParallel.
    /// </summary>
    public static async Task<PortLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(AcquisitionTimeoutMs);

        try
        {
            await Semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Failed to acquire port from pool within {AcquisitionTimeoutMs}ms. " +
                $"All {MaxParallel} parallel slots may be in use.");
        }

        var index = Interlocked.Increment(ref _nextPortIndex) % PoolSize;
        return new PortLease(BasePort + index);
    }

    internal static void Release()
    {
        Semaphore.Release();
    }
}

/// <summary>
/// Represents a leased port that must be disposed when the test completes.
/// </summary>
public sealed class PortLease : IDisposable
{
    private int _disposed;

    public int Port { get; }

    public string WebSocketUrl => $"ws://localhost:{Port}/ws";

    internal PortLease(int port)
    {
        Port = port;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            WebSocketTestPortPool.Release();
        }
    }
}

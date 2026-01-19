namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Manages a pool of ports for parallel OPC UA integration tests.
/// Tests acquire a port at startup and release it when done.
/// If all ports are in use, tests block until one becomes available.
/// </summary>
public static class OpcUaTestPortPool
{
    private const int BasePort = 4840;
    private const int PoolSize = 20;

    private static readonly SemaphoreSlim _semaphore = new(PoolSize, PoolSize);
    private static readonly bool[] _usedPorts = new bool[PoolSize];
    private static readonly object _lock = new();

    /// <summary>
    /// Acquires a port from the pool. Blocks if all ports are in use.
    /// </summary>
    /// <returns>A PortLease that must be disposed when the test is done.</returns>
    public static async Task<PortLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            for (var i = 0; i < PoolSize; i++)
            {
                if (!_usedPorts[i])
                {
                    _usedPorts[i] = true;
                    return new PortLease(BasePort + i, i);
                }
            }
        }

        // Should never happen since semaphore controls access
        throw new InvalidOperationException("No ports available despite semaphore grant.");
    }

    /// <summary>
    /// Releases a port back to the pool.
    /// </summary>
    internal static void Release(int index)
    {
        lock (_lock)
        {
            _usedPorts[index] = false;
        }
        _semaphore.Release();
    }
}

/// <summary>
/// Represents a leased port that must be disposed when the test completes.
/// </summary>
public sealed class PortLease : IDisposable
{
    private readonly int _index;
    private bool _disposed;

    public int Port { get; }

    public string ServerUrl => $"opc.tcp://localhost:{Port}";

    public string BaseAddress => $"opc.tcp://localhost:{Port}/";

    /// <summary>
    /// Gets the port-specific certificate store path for test isolation.
    /// </summary>
    public string CertificateStoreBasePath => $"pki-{Port}";

    internal PortLease(int port, int index)
    {
        Port = port;
        _index = index;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            OpcUaTestPortPool.Release(_index);
        }
    }
}

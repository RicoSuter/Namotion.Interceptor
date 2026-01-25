namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Manages a pool of ports for parallel OPC UA integration tests.
/// Tests acquire a port at startup and release it when done.
/// If all ports are in use, tests wait up to 30 seconds before timing out.
/// </summary>
public static class OpcUaTestPortPool
{
    private const int BasePort = 4840;
    private const int PoolSize = 20;
    private const int AcquisitionTimeoutMs = 10 * 60 * 1000; // 10 minutes

    private static readonly SemaphoreSlim Semaphore = new(PoolSize, PoolSize);
    private static readonly bool[] UsedPorts = new bool[PoolSize];
    private static readonly Lock Lock = new();

    /// <summary>
    /// Acquires a port from the pool. Times out after 30 seconds if all ports are in use.
    /// </summary>
    /// <returns>A PortLease that must be disposed when the test is done.</returns>
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
                $"All {PoolSize} ports may be in use. Ensure tests dispose their PortLease properly.");
        }

        lock (Lock)
        {
            for (var i = 0; i < PoolSize; i++)
            {
                if (!UsedPorts[i])
                {
                    UsedPorts[i] = true;
                    return new PortLease(BasePort + i, i);
                }
            }
        }

        // Should never happen since semaphore controls access
        Semaphore.Release();
        throw new InvalidOperationException("No ports available despite semaphore grant.");
    }

    /// <summary>
    /// Releases a port back to the pool.
    /// </summary>
    internal static void Release(int index)
    {
        lock (Lock)
        {
            UsedPorts[index] = false;
        }
        Semaphore.Release();
    }
}

/// <summary>
/// Represents a leased port that must be disposed when the test completes.
/// Cleans up certificate store on disposal to prevent accumulation.
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

            // Clean up certificate store to prevent disk accumulation
            CleanupCertificateStore();

            OpcUaTestPortPool.Release(_index);
        }
    }

    private void CleanupCertificateStore()
    {
        try
        {
            var certPath = Path.Combine(AppContext.BaseDirectory, CertificateStoreBasePath);
            if (Directory.Exists(certPath))
            {
                Directory.Delete(certPath, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures - directory may be locked by OS
            // This is non-critical as each test uses isolated directories
        }
    }
}

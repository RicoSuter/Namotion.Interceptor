namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Manages a pool of ports for parallel OPC UA integration tests.
/// Uses round-robin port assignment to avoid port reuse within a test run,
/// preventing TIME_WAIT socket issues.
/// </summary>
public static class OpcUaTestPortPool
{
    private const int BasePort = 4840;
    private const int PoolSize = 100;    // Large pool to avoid port reuse
    private const int MaxParallel = 4;   // Limit concurrent tests
    private const int AcquisitionTimeoutMs = 10 * 60 * 1000; // 10 minutes

    private static readonly SemaphoreSlim Semaphore = new(MaxParallel, MaxParallel);
    private static int _nextPortIndex = -1;

    /// <summary>
    /// Acquires a port from the pool using round-robin assignment.
    /// Limits concurrent tests to MaxParallel.
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
                $"All {MaxParallel} parallel slots may be in use. Ensure tests dispose their PortLease properly.");
        }

        // Round-robin port assignment - each test gets a unique port
        var index = Interlocked.Increment(ref _nextPortIndex) % PoolSize;
        return new PortLease(BasePort + index);
    }

    /// <summary>
    /// Releases a parallel slot back to the pool.
    /// </summary>
    internal static void Release()
    {
        Semaphore.Release();
    }
}

/// <summary>
/// Represents a leased port that must be disposed when the test completes.
/// Cleans up certificate store on disposal to prevent accumulation.
/// </summary>
public sealed class PortLease : IDisposable
{
    private bool _disposed;

    public int Port { get; }

    public string ServerUrl => $"opc.tcp://localhost:{Port}";

    public string BaseAddress => $"opc.tcp://localhost:{Port}/";

    /// <summary>
    /// Gets the port-specific certificate store path for test isolation.
    /// </summary>
    public string CertificateStoreBasePath => $"pki-{Port}";

    internal PortLease(int port)
    {
        Port = port;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Clean up certificate store to prevent disk accumulation
            CleanupCertificateStore();

            OpcUaTestPortPool.Release();
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

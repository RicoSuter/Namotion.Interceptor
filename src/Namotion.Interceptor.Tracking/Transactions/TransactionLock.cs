namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Manages per-context transaction serialization.
/// </summary>
internal sealed class TransactionLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _disposed;

    /// <summary>
    /// Acquires the transaction lock for this context.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new LockReleaser(_semaphore);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private sealed class LockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public LockReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}

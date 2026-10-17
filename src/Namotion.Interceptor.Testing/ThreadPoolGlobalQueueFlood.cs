namespace Namotion.Interceptor.Testing;

/// <summary>
/// Keeps the thread pool's global queue non-empty until disposed, so no pool thread steals work from
/// another thread's local queue.
/// </summary>
/// <remarks>
/// A pool thread takes work from its own local queue first, then from the global queue, and steals from
/// other threads' local queues only when both are empty. While this runs, an item in the local queue of a
/// thread that blocks or stays busy therefore waits for that thread, which turns a latent scheduling
/// dependency into a deterministic failure. Every pool thread spends the run executing short items, so
/// tests using this belong in a collection that disables parallelization.
/// </remarks>
public sealed class ThreadPoolGlobalQueueFlood : IDisposable
{
    private volatile bool _stopped;

    private ThreadPoolGlobalQueueFlood(int itemCount)
    {
        for (var index = 0; index < itemCount; index++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(new FloodItem(this), preferLocal: false);
        }
    }

    /// <summary>
    /// Starts the flood with enough self-requeuing items that the global queue stays non-empty however
    /// many threads the pool injects meanwhile.
    /// </summary>
    public static ThreadPoolGlobalQueueFlood Start() => new(Math.Max(256, Environment.ProcessorCount * 32));

    public void Dispose() => _stopped = true;

    private sealed class FloodItem(ThreadPoolGlobalQueueFlood owner) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            if (owner._stopped)
            {
                return;
            }

            Thread.SpinWait(100);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A pull-based subscription to receive property changes from a <see cref="PropertyChangeInterceptor"/>.
/// Each subscription owns an isolated queue. TryDequeue must be called from a single consumer thread.
/// </summary>
public sealed class PropertyChangeQueueSubscription : IDisposable
{
    private readonly PropertyChangeInterceptor _interceptor;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private volatile bool _completed;
    private int _disposed; // one-shot flag

    internal PropertyChangeQueueSubscription(PropertyChangeInterceptor interceptor)
    {
        _interceptor = interceptor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enqueue(in SubjectPropertyChange item)
    {
        if (_completed)
        {
            return;
        }

        _queue.Enqueue(item);
        _signal.Set(); // safe even after Dispose: the signal is never disposed (see Dispose)
    }

    /// <summary>Completes the subscription without unsubscribing from the interceptor (interceptor-side teardown).</summary>
    internal void Complete()
    {
        _completed = true;
        _signal.Set();
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                item = default!;
                return false;
            }

            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            _signal.Reset();

            // Re-check BOTH the queue and completion after Reset so a producer's or Complete()'s
            // Set() that raced the Reset is not lost (completion lost-wakeup fix).
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            try
            {
                _signal.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // one-shot
        }

        _completed = true;
        _signal.Set();
        _interceptor.RemoveQueueSubscription(this);

        // Deliberately do NOT dispose _signal: a concurrent producer may still call _signal.Set()
        // between its _completed check and here (enqueue-vs-dispose ObjectDisposedException fix).
        // The ManualResetEventSlim is reclaimed by GC together with this subscription.
    }
}

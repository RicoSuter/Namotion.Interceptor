using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannelSubscription : IDisposable
{
    private readonly PropertyChangedChannel _channel;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0, 1); // Binary semaphore (max count = 1)
    private volatile int _hasItems; // 0 = no items, 1 = has items
    private volatile bool _completed;

    public PropertyChangedChannelSubscription(PropertyChangedChannel channel)
    {
        _channel = channel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(SubjectPropertyChange item)
    {
        if (_completed)
        {
            return;
        }

        _queue.Enqueue(item);
        
        // Use binary signaling: only signal if transitioning from 0 to 1
        // This prevents semaphore overflow with large queues
        if (Interlocked.CompareExchange(ref _hasItems, 1, 0) == 0)
        {
            _semaphore.Release();
        }
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken ct = default)
    {
        while (true)
        {
            // Try to dequeue first in case items are already available
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            // Check completion after failed dequeue
            if (_completed)
            {
                item = default!;
                return false;
            }

            // Mark as no items before waiting
            // This must be done BEFORE checking the queue again
            Interlocked.Exchange(ref _hasItems, 0);
            
            // Double-check queue after marking as no items
            // This prevents race where item is enqueued after our first TryDequeue
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            // Queue is empty, wait for signal
            try
            {
                _semaphore.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
            
            // After being signaled, loop back to try dequeuing again
        }
    }

    public void Dispose()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        
        // Signal to wake up any waiting TryDequeue calls
        // Use CompareExchange to avoid semaphore overflow
        if (Interlocked.CompareExchange(ref _hasItems, 1, 0) == 0)
        {
            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // Should not happen with binary semaphore, but handle gracefully
            }
        }

        _channel.Unsubscribe(this);
        _semaphore.Dispose();
    }
}
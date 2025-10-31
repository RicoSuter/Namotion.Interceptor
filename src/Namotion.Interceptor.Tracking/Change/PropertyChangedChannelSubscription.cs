using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannelSubscription : IDisposable
{
    private readonly PropertyChangedChannel _channel;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
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
        
        // Signal that an item is available
        // SemaphoreSlim will handle the count internally
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Should not happen with semaphore(0) initial count
            // but handle gracefully if it does
        }
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken ct = default)
    {
        while (true)
        {
            // First check completion to avoid waiting if already done
            if (_completed)
            {
                // Drain any remaining items before returning false
                if (_queue.TryDequeue(out item))
                {
                    return true;
                }
                
                item = default!;
                return false;
            }

            // Wait for signal that items are available
            try
            {
                _semaphore.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }

            // After being signaled, try to dequeue
            // This matches the Release() call in Enqueue
            if (_queue.TryDequeue(out item))
            {
                return true;
            }
            
            // If queue is empty after being signaled, it might be a completion signal
            // Loop back to check _completed at the top
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
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled, ignore
        }

        _channel.Unsubscribe(this);
        _semaphore.Dispose();
    }
}
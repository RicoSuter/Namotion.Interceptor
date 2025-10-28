using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannelSubscription : IDisposable
{
    private readonly PropertyChangedChannel _channel;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
    private volatile int _pendingCount;
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
        
        // Only release semaphore if we successfully increment the pending count
        // This prevents semaphore overflow by capping releases
        if (Interlocked.Increment(ref _pendingCount) == 1)
        {
            _semaphore.Release();
        }
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken ct = default)
    {
        while (true)
        {
            // Drain the queue first before waiting
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

            // Reset pending count before waiting since queue is confirmed empty
            Interlocked.Exchange(ref _pendingCount, 0);
            try
            {
                _semaphore.Wait(ct);
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
        if (_completed)
        {
            return;
        }

        _completed = true;
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
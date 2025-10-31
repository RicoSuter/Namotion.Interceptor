using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannelSubscription : IDisposable
{
    private readonly PropertyChangedChannel _channel;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false); // non-counting signal
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
        _signal.Set(); // wake consumer (idempotent if already set)
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken ct = default)
    {
        while (true)
        {
            // Fast path: dequeue if available
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            // Short spin to handle bursts without kernel waits
            var spinner = new SpinWait();
            while (!spinner.NextSpinWillYield)
            {
                spinner.SpinOnce();
                if (_queue.TryDequeue(out item))
                {
                    return true;
                }
                if (_completed)
                {
                    item = default!;
                    return false;
                }
            }

            // Reset the signal and re-check via TryDequeue to avoid lost wake-ups
            _signal.Reset();
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            // Still empty after reset: wait for a producer to Set()
            try
            {
                _signal.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
            // loop and try dequeuing again
        }
    }

    public void Dispose()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        // Wake any waiting TryDequeue
        _signal.Set();

        _channel.Unsubscribe(this);
        _signal.Dispose();
    }
}
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A subscription to receive property changes from a PropertyChangeInterceptor.
/// Each subscription maintains its own isolated queue.
/// Thread-safe for concurrent Enqueue calls from multiple threads.
/// Consuming (TryDequeue, TryDequeueImmediate, WaitToDequeueAsync) is single-consumer per subscription.
/// </summary>
public sealed class PropertyChangeQueueSubscription : IDisposable
{
    private const int NoWaiter = 0;
    private const int BlockingWaiter = 1;
    private const int AsyncWaiter = 2;

    // Cleared on disposal (doubles as the one-shot dispose flag) so a retained handle does not
    // directly pin the interceptor and its other consumers. Buffered changes can retain their
    // subjects and contexts until they are drained or the handle itself is collected.
    private PropertyChangeInterceptor? _interceptor;

    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false); // non-counting signal
    private readonly AsyncDequeueWaiter _asyncWaiter = new();
    private int _waiter; // the armed consumer wait; whoever exchanges it back to NoWaiter owns the wake-up
    private volatile bool _completed;

    internal PropertyChangeQueueSubscription(PropertyChangeInterceptor interceptor)
    {
        _interceptor = interceptor;
    }

    /// <summary>
    /// Number of changes currently queued. Exact only from the consumer thread while no
    /// producers are racing; concurrent enqueues may or may not be included in the snapshot.
    /// </summary>
    public int Count => _queue.Count;

    /// <summary>
    /// Dequeues one currently-available change without waiting; returns false when the queue is
    /// momentarily empty.
    /// </summary>
    /// <remarks>
    /// Single consumer: it must never run concurrently with <see cref="TryDequeue"/> or another drain loop
    /// on the same subscription. Breaking that is silent rather than fatal. Nothing throws; changes are
    /// simply consumed by the wrong loop, and the symptom appears later as a source that has quietly
    /// stopped delivering. Use it to drain a subscription you own exclusively at that moment, for example
    /// while connecting, or paired with <see cref="WaitToDequeueAsync"/> in an asynchronous drain loop.
    /// <para>
    /// This is for a hand-rolled drain loop, not for feeding the built-in change processor: that
    /// processor creates and owns its own subscription and does not expose it, so there is nothing there
    /// for this to drain. Returns false both when the queue is momentarily empty and when the
    /// subscription has been disposed, so a polling loop needs its own stop condition.
    /// </para>
    /// </remarks>
    public bool TryDequeueImmediate(out SubjectPropertyChange item) => _queue.TryDequeue(out item);

    /// <summary>
    /// Enqueues a property change. Thread-safe and can be called concurrently from multiple threads.
    /// </summary>
    /// <param name="item">The property change to enqueue.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enqueue(in SubjectPropertyChange item)
    {
        if (_completed)
        {
            return;
        }

        _queue.Enqueue(item); // copy happens here into the queue

        // Pairs with the full fence of the consumer's arming exchange: either this read sees the armed
        // waiter, or the consumer's re-check after arming sees the item.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _waiter) != NoWaiter)
        {
            WakeConsumer();
        }
    }

    private void WakeConsumer()
    {
        switch (Interlocked.Exchange(ref _waiter, NoWaiter))
        {
            case BlockingWaiter:
                _signal.Set();
                break;
            case AsyncWaiter:
                _asyncWaiter.SetResult(true);
                break;
        }
    }

    /// <summary>
    /// Attempts to dequeue a property change, waiting if the queue is empty.
    /// Should only be called from a single consumer thread per subscription (not thread-safe for concurrent TryDequeue calls).
    /// </summary>
    /// <param name="item">The dequeued property change if available.</param>
    /// <param name="cancellationToken">Cancellation token to abort the wait.</param>
    /// <returns>True if an item was dequeued; false when cancellation is requested, or when the
    /// subscription is completed and its queue is empty.</returns>
    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Check cancellation before dequeuing so that kill/shutdown signals
            // are observed even when the queue is continuously fed by producers.
            if (cancellationToken.IsCancellationRequested)
            {
                item = default!;
                return false;
            }

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

            // Reset before arming so a wake-up claimed after arming is not undone; the re-check after
            // arming catches an enqueue or Dispose() that raced it. A late Set() from an earlier arming
            // only causes a spurious loop. Disarming is a plain store: only this consumer arms.
            _signal.Reset();
            Interlocked.Exchange(ref _waiter, BlockingWaiter);
            if (!_queue.IsEmpty || _completed)
            {
                Volatile.Write(ref _waiter, NoWaiter);
                continue;
            }

            try
            {
                _signal.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _waiter, NoWaiter);
                item = default!;
                return false;
            }
            // loop and try dequeuing again
        }
    }

    /// <summary>
    /// Waits without blocking a thread until a change may be available, for a consumer that drains with
    /// <see cref="TryDequeueImmediate"/>. Single consumer: it must not run concurrently with
    /// <see cref="TryDequeue"/> or another wait, and each returned task must be awaited once before the
    /// next wait starts. A waiting consumer never resumes inline on the writing thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the wait.</param>
    /// <returns>True when a change may be available, so the caller re-checks; false when cancellation is
    /// requested, or when the subscription is completed and its queue is empty.</returns>
    public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || (_completed && _queue.IsEmpty))
        {
            return new ValueTask<bool>(false);
        }

        var version = _asyncWaiter.Reset();
        Interlocked.Exchange(ref _waiter, AsyncWaiter);
        if (!_queue.IsEmpty || _completed)
        {
            // A failed disarm means a waker already claimed the wait and is completing it.
            return Interlocked.CompareExchange(ref _waiter, NoWaiter, AsyncWaiter) == AsyncWaiter
                ? new ValueTask<bool>(true)
                : new ValueTask<bool>(_asyncWaiter, version);
        }

        // Registered after arming, so a token cancelled in between still claims the waiter (inline).
        _asyncWaiter.SetCancellationRegistration(cancellationToken.UnsafeRegister(
            static state =>
            {
                var subscription = (PropertyChangeQueueSubscription)state!;
                if (Interlocked.CompareExchange(ref subscription._waiter, NoWaiter, AsyncWaiter) == AsyncWaiter)
                {
                    subscription._asyncWaiter.SetResult(false);
                }
            },
            this));

        return new ValueTask<bool>(_asyncWaiter, version);
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _interceptor, null);
        if (owner is null)
        {
            return; // one-shot
        }

        _completed = true;
        WakeConsumer();

        owner.RemoveQueueSubscription(this);

        // Deliberately not disposing _signal: a concurrent producer may still call _signal.Set() after its _completed check (enqueue-vs-dispose fix).
    }

    private sealed class AsyncDequeueWaiter : IValueTaskSource<bool>
    {
        // Completed from a producer's property write, which must not run the consumer's loop inline.
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _cancellation;

        public short Reset()
        {
            _core.Reset();
            return _core.Version;
        }

        public void SetCancellationRegistration(CancellationTokenRegistration cancellation) => _cancellation = cancellation;

        public void SetResult(bool result) => _core.SetResult(result);

        public bool GetResult(short token)
        {
            // Dispose waits out a running callback, so no stale callback can complete the next wait.
            _cancellation.Dispose();
            _cancellation = default;
            return _core.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}

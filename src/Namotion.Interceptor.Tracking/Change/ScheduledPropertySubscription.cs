using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A per-property subscription whose deliveries run serially on a scheduler. Observer and scheduler
/// exceptions are isolated from the writer and reported through the configured error handler.
/// </summary>
public sealed class ScheduledPropertySubscription : IDisposable
{
    private const int Live = 0;
    private const int Disposed = 1;
    private const int Faulted = 2;

    internal const int MaxBatch = 1024;

    private static readonly Func<IScheduler, ScheduledPropertySubscription, IDisposable> DrainAction =
        static (_, subscription) =>
        {
            subscription.Drain();
            return Disposable.Empty;
        };

    private ConcurrentQueue<SubjectPropertyChange>? _queue = new();
    private IPropertyChangeObserver? _observer;
    private Action<Exception>? _onError;
    private IDisposable? _upstream;
    private IScheduler? _scheduler;
    private int _state;
    private int _wip;

    private ScheduledPropertySubscription(
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError)
    {
        _observer = observer;
        _scheduler = scheduler;
        _onError = onError;
    }

    /// <summary>
    /// Gets the number of accepted changes that have not yet been dequeued. The queue is unbounded and the
    /// value is exact only when writes and deliveries are quiescent.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _queue)?.Count ?? 0;

    /// <summary>
    /// Gets whether a scheduler failure permanently faulted the subscription. Deliberate disposal is not a
    /// fault.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _state) == Faulted;

    internal int WorkInProgressForTests => Volatile.Read(ref _wip);

    internal bool IsObserverReleasedForTests => Volatile.Read(ref _observer) is null;

    internal static ScheduledPropertySubscription Create(
        PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError)
    {
        var subscription = new ScheduledPropertySubscription(observer, scheduler, onError);
        var upstream = property.SubscribeInline(new Forwarder(subscription));

        Interlocked.Exchange(ref subscription._upstream, upstream);
        if (Volatile.Read(ref subscription._state) != Live)
        {
            Interlocked.Exchange(ref subscription._upstream, null)?.Dispose();
        }

        return subscription;
    }

    private void Enqueue(in SubjectPropertyChange change)
    {
        if (Volatile.Read(ref _state) != Live)
        {
            return;
        }

        var queue = Volatile.Read(ref _queue);
        if (queue is null)
        {
            return;
        }

        queue.Enqueue(change);
        if (Interlocked.Increment(ref _wip) == 1)
        {
            ScheduleDrain();
        }
    }

    private void Drain()
    {
        var processed = 0;
        try
        {
            var pending = Volatile.Read(ref _wip);
            while (processed < MaxBatch)
            {
                if (processed >= pending)
                {
                    pending = Volatile.Read(ref _wip);
                    if (processed >= pending)
                    {
                        break;
                    }
                }

                if (Volatile.Read(ref _state) != Live)
                {
                    return;
                }

                var queue = Volatile.Read(ref _queue);
                if (queue is null || !queue.TryDequeue(out var change))
                {
                    break;
                }

                processed++;
                Deliver(in change);
            }
        }
        finally
        {
            if (Interlocked.Add(ref _wip, -processed) != 0 &&
                Volatile.Read(ref _state) == Live)
            {
                ScheduleDrain();
            }
        }
    }

    private void ScheduleDrain()
    {
        var scheduler = Volatile.Read(ref _scheduler);
        if (scheduler is null)
        {
            return;
        }

        var onError = Volatile.Read(ref _onError);
        try
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                scheduler.Schedule(this, DrainAction);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    scheduler.Schedule(this, DrainAction);
                }
            }
        }
        catch (Exception exception)
        {
            try
            {
                TransitionOutOfLive(Faulted);
            }
            catch
            {
            }

            ReportError(onError, exception);
        }
    }

    private void Deliver(in SubjectPropertyChange change)
    {
        var observer = Volatile.Read(ref _observer);
        if (observer is null)
        {
            return;
        }

        var onError = Volatile.Read(ref _onError);
        try
        {
            observer.OnChange(in change);
        }
        catch (Exception exception)
        {
            ReportError(onError, exception);
        }
    }

    private static void ReportError(Action<Exception>? onError, Exception exception)
    {
        try
        {
            onError?.Invoke(exception);
        }
        catch
        {
        }
    }

    private void TransitionOutOfLive(int target)
    {
        if (Interlocked.CompareExchange(ref _state, target, Live) != Live)
        {
            return;
        }

        Volatile.Write(ref _observer, null);
        Volatile.Write(ref _onError, null);
        Volatile.Write(ref _scheduler, null);
        Volatile.Write(ref _queue, null);
        Interlocked.Exchange(ref _upstream, null)?.Dispose();
    }

    /// <summary>
    /// Stops acceptance and delivery, releases the observer, scheduler, and upstream subscription, and drops
    /// all queued changes. An observer call already in progress may finish after this method returns.
    /// </summary>
    public void Dispose() => TransitionOutOfLive(Disposed);

    private sealed class Forwarder(ScheduledPropertySubscription owner) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => owner.Enqueue(in change);
    }
}

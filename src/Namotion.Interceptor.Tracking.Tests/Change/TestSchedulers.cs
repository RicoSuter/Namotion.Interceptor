using System.Reactive.Concurrency;
using System.Reactive.Disposables;

using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Runs scheduled work only when the test pumps it, so an interleaving is chosen rather than raced for.
/// </summary>
internal sealed class ControllableScheduler : IScheduler
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public int QueuedCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        lock (_gate)
        {
            _queue.Enqueue(() => action(this, state));
        }

        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    /// <summary>Runs at most one queued work item. Returns false when the queue was empty.</summary>
    public bool RunOne()
    {
        Action? work;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            work = _queue.Dequeue();
        }

        work();
        return true;
    }

    /// <summary>Runs every item queued at entry, without following items those items queue.</summary>
    public int RunAll()
    {
        int budget;
        lock (_gate)
        {
            budget = _queue.Count;
        }

        // Dequeued one at a time so an item that throws leaves its untouched siblings queued.
        var ran = 0;
        while (ran < budget)
        {
            Action work;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    break;
                }

                work = _queue.Dequeue();
            }

            ran++;
            work();
        }

        return ran;
    }

    /// <summary>
    /// Runs items, including ones scheduled by earlier items, until nothing is left or the budget below is
    /// spent. The budget is what turns a drain that reschedules itself without making progress into a red
    /// test instead of a hung test run, which is otherwise the only symptom such a regression has.
    /// </summary>
    public void RunUntilIdle()
    {
        // Eight full batches: far more work items than any test here queues, and still bounded. Naming
        // ScheduledPropertySubscription for the batch size does not make this a per-property test file, so
        // PerPropertySubscriptionCollection membership does not apply: it holds no tests and subscribes to
        // nothing.
        const int budget = ScheduledPropertySubscription.MaxBatch * 8;

        var ran = 0;
        while (RunOne())
        {
            if (++ran > budget)
            {
                throw new InvalidOperationException(
                    $"RunUntilIdle ran more than {budget} work items without the queue going idle, " +
                    "which means a self-sustaining reschedule loop rather than slow progress.");
            }
        }
    }
}

/// <summary>Reproduces a scheduler disposed before the subscription: Schedule throws on the writer thread.</summary>
internal sealed class ThrowingScheduler : IScheduler
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));
}

/// <summary>
/// Reproduces a scheduler disposed while a drain was already queued: Schedule succeeds and the work item
/// never runs. This is the half of the scheduler-failure space the design cannot recover from.
/// </summary>
internal sealed class BlackHoleScheduler : IScheduler
{
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}

/// <summary>
/// Wraps a scheduler and records anything that escapes a work item. On a real pool scheduler such an escape
/// is unhandled and terminates the test host, so it can only be asserted by catching it here first.
/// </summary>
internal sealed class RecordingScheduler(IScheduler inner) : IScheduler
{
    private readonly List<Exception> _escaped = [];
    private int _scheduleCallCount;

    public DateTimeOffset Now => inner.Now;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IReadOnlyList<Exception> Escaped
    {
        get { lock (_escaped) { return _escaped.ToArray(); } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return inner.Schedule(state, (_, innerState) =>
        {
            try
            {
                // The work item gets this wrapper, not the inner scheduler, so anything it schedules
                // from inside itself is still counted and still has its exceptions recorded.
                return action(this, innerState);
            }
            catch (Exception exception)
            {
                lock (_escaped)
                {
                    _escaped.Add(exception);
                }

                return Disposable.Empty;
            }
        });
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}

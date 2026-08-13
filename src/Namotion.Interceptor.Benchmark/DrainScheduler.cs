using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// Delegates to <see cref="Scheduler.Default"/>, counts the drain work items it hands over, and can hold the
/// pending one back so a producer builds a backlog before the drain ever starts. Holding is the only way to
/// get a backlog out of a no-op observer, which otherwise outruns any single writer.
/// </summary>
internal sealed class DrainScheduler : IScheduler
{
    private readonly object _gate = new();
    private bool _held;
    private Action? _heldWorkItem;
    private long _workItemCount;

    public long WorkItemCount => Interlocked.Read(ref _workItemCount);

    public DateTimeOffset Now => Scheduler.Default.Now;

    public void Hold()
    {
        lock (_gate)
        {
            _held = true;
        }
    }

    public void Release()
    {
        Action? workItem;
        lock (_gate)
        {
            _held = false;
            workItem = _heldWorkItem;
            _heldWorkItem = null;
        }

        workItem?.Invoke();
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        lock (_gate)
        {
            if (_held)
            {
                // At most one drain work item exists at a time (only the zero to one transition of the
                // dispatcher's counter schedules), so a single slot cannot drop one.
                _heldWorkItem = () => Forward(state, action);
                return Disposable.Empty;
            }
        }

        return Forward(state, action);
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Scheduler.Default.Schedule(state, dueTime, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Scheduler.Default.Schedule(state, dueTime, action);

    private IDisposable Forward<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _workItemCount);
        return Scheduler.Default.Schedule(state, action);
    }
}

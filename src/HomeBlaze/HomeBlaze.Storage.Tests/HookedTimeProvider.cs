using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A clock that runs code of the test when a <see cref="ReconcileTrigger"/> creates or disposes a timer, on the
/// thread that does so. That puts other work between two steps of a connect, which nothing else can reach.
/// </summary>
internal sealed class HookedTimeProvider(ManualTimeProvider inner) : TimeProvider
{
    private Action? _onNextTimerCreated;
    private Action? _onNextTimerDisposed;

    public override long TimestampFrequency => inner.TimestampFrequency;

    /// <summary>Runs the action once, before the next timer of a trigger is created.</summary>
    public void BeforeNextTimerIsCreated(Action action) => Volatile.Write(ref _onNextTimerCreated, action);

    /// <summary>Runs the action once, before the next timer of a trigger is disposed.</summary>
    public void BeforeNextTimerIsDisposed(Action action) => Volatile.Write(ref _onNextTimerDisposed, action);

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (state is not ReconcileTrigger)
        {
            return inner.CreateTimer(callback, state, dueTime, period);
        }

        Interlocked.Exchange(ref _onNextTimerCreated, null)?.Invoke();
        return new HookedTimer(inner.CreateTimer(callback, state, dueTime, period), this);
    }

    private sealed class HookedTimer(ITimer timer, HookedTimeProvider timeProvider) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            Interlocked.Exchange(ref timeProvider._onNextTimerDisposed, null)?.Invoke();
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

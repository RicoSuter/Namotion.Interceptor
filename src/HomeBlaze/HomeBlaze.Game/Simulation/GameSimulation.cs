using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Runs the actions of one game one after another on a fixed tick. Any thread may post or invoke actions without
/// locking; the actions, schedules and tick handlers run on the thread that runs the tick, which <see cref="Current"/>
/// marks meanwhile. A tick runs only what was queued when it started, so work queued or scheduled by an action waits
/// for the next tick.
/// </summary>
internal sealed class GameSimulation(TimeProvider timeProvider, ILogger logger)
{
    private const string StoppedMessage = "The game has stopped.";

    private static readonly TimeSpan RepeatedWarningInterval = TimeSpan.FromMinutes(1);

    [ThreadStatic]
    private static GameSimulation? t_current;

    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly GameSchedules _schedules = new();
    private readonly List<Action> _tickHandlers = [];
    private long _tickTimestamp;
    private bool _hasTicked;
    private volatile bool _isStopped;
    private int _isDrained;

    // Failure logging: see RunLogged.
    private bool _isFailing;
    private bool _hasFailedInTick;
    private long _lastWarningTimestamp;
    private int _suppressedCount;

    /// <summary>
    /// The simulation whose tick runs on this thread, or null.
    /// </summary>
    public static GameSimulation? Current => t_current;

    /// <summary>
    /// Whether this simulation's tick runs on the calling thread.
    /// </summary>
    public bool IsCurrent => ReferenceEquals(t_current, this);

    /// <summary>
    /// Queues the action for the next tick; a throwing action is logged. Does nothing once the simulation has stopped.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_isStopped)
        {
            _queue.Enqueue(new Item(action, null));
        }
    }

    /// <summary>
    /// Queues the function for the next tick and completes with its result or exception. Canceling the token completes
    /// the task as canceled at once and drops the function. On the simulation thread the function runs inline instead.
    /// </summary>
    /// <returns>A faulted task with an <see cref="InvalidOperationException"/> once the simulation has stopped.</returns>
    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (IsCurrent)
        {
            return InvokeInline(function, cancellationToken);
        }

        if (_isStopped)
        {
            return Task.FromException<T>(new InvalidOperationException(StoppedMessage));
        }

        var work = new InvokeWork<T>(function, cancellationToken);
        work.RegisterCancellation();
        if (work.Task.IsCompleted)
        {
            return work.Task;
        }

        _queue.Enqueue(new Item(null, work));

        // Pairs with the Interlocked.Exchange in Stop: the full fence makes either its sweep see the work or this see the flag.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _isDrained) == 1)
        {
            work.Abandon();
        }

        return work.Task;
    }

    /// <summary>
    /// Runs the action on the simulation thread at the first tick at or after the delay, counted from the start of the
    /// current tick and never in the tick that schedules it; call on the simulation thread.
    /// </summary>
    /// <returns>A handle whose disposal, from any thread, keeps the action from running.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    /// <exception cref="InvalidOperationException">Called outside the simulation thread.</exception>
    /// <remarks>
    /// A delay too long to represent never comes due.
    /// </remarks>
    public IDisposable Schedule(object owner, TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        EnsureCurrent();

        var delayTimestamp = delay.TotalSeconds * timeProvider.TimestampFrequency;
        var due = delayTimestamp >= long.MaxValue - _tickTimestamp ? long.MaxValue : _tickTimestamp + (long)delayTimestamp;
        return _schedules.Add(owner, due, action);
    }

    /// <summary>
    /// Cancels every schedule of the owner; call on the simulation thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called outside the simulation thread.</exception>
    public void CancelSchedules(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureCurrent();
        _schedules.Cancel(owner);
    }

    /// <summary>
    /// Adds a step that every tick runs after the schedules and actions, in the order added.
    /// </summary>
    /// <exception cref="InvalidOperationException">The first tick already ran.</exception>
    public void AddTickHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_hasTicked)
        {
            throw new InvalidOperationException("Add tick handlers before the first tick.");
        }

        _tickHandlers.Add(handler);
    }

    /// <summary>
    /// Runs one tick on the calling thread: the due schedules, the actions queued before the tick, then the tick handlers.
    /// </summary>
    public void RunTick()
    {
        var previous = t_current;
        t_current = this;
        _hasTicked = true;
        _hasFailedInTick = false;
        try
        {
            _tickTimestamp = timeProvider.GetTimestamp();
            _schedules.BeginTick(_tickTimestamp);
            while (_schedules.TryTakeDue(_tickTimestamp, out var scheduled))
            {
                RunLogged(scheduled);
            }

            for (var remaining = _queue.Count; remaining > 0 && _queue.TryDequeue(out var item); remaining--)
            {
                if (item.Work is { } work)
                {
                    work.Run();
                }
                else
                {
                    RunLogged(item.Action!);
                }
            }

            foreach (var handler in _tickHandlers)
            {
                RunLogged(handler);
            }
        }
        finally
        {
            _isFailing = _hasFailedInTick;
            t_current = previous;
        }
    }

    /// <summary>
    /// Ticks every <see cref="GameSimulationSettings.TickInterval"/> until canceled, then runs one final tick for the work
    /// queued meanwhile and refuses all later work.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(GameSimulationSettings.TickInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RunTick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
        finally
        {
            Stop();
        }
    }

    private void Stop()
    {
        _isStopped = true;
        try
        {
            RunTick();
        }
        finally
        {
            Interlocked.Exchange(ref _isDrained, 1);
            while (_queue.TryDequeue(out var item))
            {
                item.Work?.Abandon();
            }
        }
    }

    private static Task<T> InvokeInline<T>(Func<T> function, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        try
        {
            return Task.FromResult(function());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    // A streak of ticks with failures warns once, then stays at debug level so a broken object type does not flood the
    // log at the tick rate. It warns again after a minute, with the count it suppressed, so a second broken object type
    // is not hidden for good. A tick without failure ends the streak, whatever else ran in it.
    private void RunLogged(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _hasFailedInTick = true;
            var now = timeProvider.GetTimestamp();
            if (!_isFailing || timeProvider.GetElapsedTime(_lastWarningTimestamp, now) >= RepeatedWarningInterval)
            {
                logger.LogWarning(
                    exception, "An action of the game simulation failed: {Action} ({SuppressedCount} similar failures suppressed since the last warning)",
                    Describe(action), _suppressedCount);
                _lastWarningTimestamp = now;
                _suppressedCount = 0;
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "An action of the game simulation failed: {Action}", Describe(action));
                }

                _suppressedCount++;
            }

            _isFailing = true;
        }
    }

    private static string Describe(Action action) => $"{action.Method.DeclaringType?.FullName}.{action.Method.Name}";

    private void EnsureCurrent()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("Call this on the simulation thread.");
        }
    }

    private interface IGameWork
    {
        void Run();

        void Abandon();
    }

    private readonly record struct Item(Action? Action, IGameWork? Work);

    private sealed class InvokeWork<T>(Func<T> function, CancellationToken cancellationToken)
        : TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously), IGameWork
    {
        private CancellationTokenRegistration _registration;

        public void RegisterCancellation()
        {
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.UnsafeRegister(static state => ((InvokeWork<T>)state!).Cancel(), this);
            }
        }

        private void Cancel() => TrySetCanceled(cancellationToken);

        public void Run()
        {
            _registration.Dispose();
            if (Task.IsCompleted)
            {
                return;
            }

            try
            {
                TrySetResult(function());
            }
            catch (Exception exception)
            {
                TrySetException(exception);
            }
        }

        public void Abandon()
        {
            _registration.Dispose();
            TrySetException(new InvalidOperationException(StoppedMessage));
        }
    }
}

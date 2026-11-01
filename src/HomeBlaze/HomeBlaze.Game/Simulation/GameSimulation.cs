using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Runs the actions of one game one after another on a fixed tick. Any thread may post or invoke actions; the actions,
/// schedules and tick handlers run on the thread that runs the tick, which <see cref="Current"/> marks meanwhile.
/// </summary>
internal sealed class GameSimulation(TimeProvider timeProvider, ILogger logger)
{
    [ThreadStatic]
    private static GameSimulation? t_current;

    private readonly Channel<Action> _actions = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly GameSchedules _schedules = new();
    private readonly List<Action> _tickHandlers = [];
    private bool _isFailing;
    private bool _hasFailedInTick;

    /// <summary>
    /// The simulation whose tick runs on this thread, or null.
    /// </summary>
    public static GameSimulation? Current => t_current;

    /// <summary>
    /// Whether this simulation's tick runs on the calling thread.
    /// </summary>
    public bool IsCurrent => ReferenceEquals(t_current, this);

    /// <summary>
    /// Queues the action for the next tick; a throwing action is logged.
    /// </summary>
    public void Post(Action action) => _actions.Writer.TryWrite(action);

    /// <summary>
    /// Queues the function for the next tick and completes with its result or exception. Canceling before the tick drops it.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(function());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Runs the action on the simulation thread at the first tick at or after the delay; call on the simulation thread.
    /// </summary>
    /// <returns>A handle whose disposal, from any thread, keeps the action from running.</returns>
    /// <exception cref="InvalidOperationException">Called outside the simulation thread.</exception>
    public IDisposable Schedule(object owner, TimeSpan delay, Action action)
    {
        EnsureCurrent();
        var due = timeProvider.GetTimestamp() + (long)(delay.TotalSeconds * timeProvider.TimestampFrequency);
        return _schedules.Add(owner, due, action);
    }

    /// <summary>
    /// Cancels every schedule of the owner; call on the simulation thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called outside the simulation thread.</exception>
    public void CancelSchedules(object owner)
    {
        EnsureCurrent();
        _schedules.Cancel(owner);
    }

    /// <summary>
    /// Adds a step that every tick runs after the schedules and actions, in the order added; call before the first tick.
    /// </summary>
    public void AddTickHandler(Action handler) => _tickHandlers.Add(handler);

    /// <summary>
    /// Runs one tick on the calling thread: the due schedules, the queued actions, then the tick handlers.
    /// </summary>
    public void RunTick()
    {
        t_current = this;
        _hasFailedInTick = false;
        try
        {
            var now = timeProvider.GetTimestamp();
            while (_schedules.TryTakeDue(now, out var scheduled))
            {
                RunLogged(scheduled);
            }

            while (_actions.Reader.TryRead(out var action))
            {
                RunLogged(action);
            }

            foreach (var handler in _tickHandlers)
            {
                RunLogged(handler);
            }
        }
        finally
        {
            _isFailing = _hasFailedInTick;
            t_current = null;
        }
    }

    /// <summary>
    /// Ticks every <see cref="GameSimulationSettings.TickInterval"/> until canceled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GameSimulationSettings.TickInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RunTick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
    }

    // Warns when failures start and stays at debug level while every tick has one, so a broken object type does not
    // flood the log at the tick rate; a tick without failure ends the streak, whatever else ran in it.
    private void RunLogged(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            logger.Log(_isFailing || _hasFailedInTick ? LogLevel.Debug : LogLevel.Warning, exception, "An action of the game simulation failed");
            _hasFailedInTick = true;
        }
    }

    private void EnsureCurrent()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("Call this on the simulation thread.");
        }
    }
}

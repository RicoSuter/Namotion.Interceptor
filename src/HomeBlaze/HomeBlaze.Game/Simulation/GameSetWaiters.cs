namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The tasks of commands that complete only once a later write of the object set published their result, in the order
/// the commands ran. Used only on the simulation thread, except <see cref="InvokeAsync"/>.
/// </summary>
internal sealed class GameSetWaiters
{
    private readonly List<(long ObjectSetVersion, TaskCompletionSource Completion)> _waiters = [];

    /// <summary>
    /// Runs the command in the simulation's next tick, then waits for a later write of the object set; call outside the
    /// simulation thread.
    /// </summary>
    /// <returns>A task that fails with the command's exception, is canceled when the token is canceled before the command
    /// ran, and otherwise completes once <see cref="Complete"/> sees a later object set version.</returns>
    public Task InvokeAsync(GameSimulation simulation, GamePublisher publisher, Action command, CancellationToken cancellationToken)
    {
        // Completed directly rather than through Unwrap, whose proxy completes only once a continuation ran.
        var completion = new CommandCompletion(cancellationToken);
        simulation.InvokeAsync(() =>
        {
            try
            {
                command();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                return false;
            }

            _waiters.Add((publisher.ObjectSetVersion, completion));
            return true;
        }, cancellationToken).ContinueWith(
            static (invoke, state) => ForwardUnfinished(invoke, (CommandCompletion)state!),
            completion, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    /// <summary>
    /// Completes the waiters added before the object set version was reached.
    /// </summary>
    public void Complete(long objectSetVersion)
    {
        var count = 0;
        while (count < _waiters.Count && _waiters[count].ObjectSetVersion < objectSetVersion)
        {
            _waiters[count].Completion.TrySetResult();
            count++;
        }

        _waiters.RemoveRange(0, count);
    }

    // A command canceled before it ran, or refused because the game stopped.
    private static void ForwardUnfinished(Task invoke, CommandCompletion completion)
    {
        if (invoke.IsCanceled)
        {
            completion.TrySetCanceled(completion.CancellationToken);
        }
        else
        {
            completion.TrySetException(invoke.Exception!.InnerExceptions);
        }
    }

    /// <summary>
    /// Fails every waiter with a <see cref="GameStoppedException"/>.
    /// </summary>
    public void Abandon()
    {
        foreach (var (_, completion) in _waiters)
        {
            completion.TrySetException(new GameStoppedException());
        }

        _waiters.Clear();
    }

    // Carries the caller's token, so a command canceled before it ran fails with an exception that names it.
    private sealed class CommandCompletion(CancellationToken cancellationToken)
        : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
    }
}

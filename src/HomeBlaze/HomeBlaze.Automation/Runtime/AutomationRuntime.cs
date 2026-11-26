using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The worker of one automation. Callbacks on writing threads only enqueue: property and graph changes from
/// <see cref="AutomationChangeReceiver"/> and ticks from <see cref="WakeTimer"/>. The worker processes one input at a time
/// with <see cref="AutomationEngine"/>, so no rule runs inside a property setter, and sets the timer after each input to
/// when the engine is due next.
/// </summary>
internal sealed class AutomationRuntime
{
    /// <summary>How long a stop waits for the worker to end before it abandons the worker.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Automation _automation;
    private readonly ILogger _logger;
    private readonly AutomationStateKeeper _keeper;
    private readonly AutomationInputQueue _queue = new();
    private readonly AutomationEngine _engine;
    private readonly AutomationChangeReceiver _changes;
    private readonly GraphRoutes _routes;
    private CheckpointLocation _location;
    private int _isTickPending;
    private WakeTimer? _wakeTimer;

    /// <summary>Creates the worker of the automation, whose checkpoint is at the path the automation has now.</summary>
    public AutomationRuntime(Automation automation, ILogger logger)
    {
        var location = CheckpointLocation.Of(automation, logger);
        _automation = automation;
        _logger = logger;
        _location = location;
        _keeper = new AutomationStateKeeper(location.Store);
        _changes = new AutomationChangeReceiver(_queue);
        _routes = new GraphRoutes(automation, _changes);
        _engine = new AutomationEngine(automation, _queue, _keeper, logger, _changes, _routes);
    }

    // White-box test hook: the graph routes of the stored resolutions.
    internal GraphRoutes Routes => _routes;

    /// <summary>The checkpoint of the automation: where it started, or where it moved to later. Changed by the worker only.</summary>
    public CheckpointLocation Location
    {
        get => Volatile.Read(ref _location);
        private set => Volatile.Write(ref _location, value);
    }

    // Keeps the token sources out of the automation, which is at the coupling limit of the analyzer.
    /// <summary>Runs the stop with a token that is canceled with <paramref name="cancellationToken"/> or after <see cref="StopTimeout"/>.</summary>
    public static async Task WaitForStopAsync(Func<CancellationToken, Task> stop, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(StopTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await stop(linked.Token);
    }

    /// <summary>Gives up the checkpoint file once the worker ended, so that another worker of the path owns it.</summary>
    public void Release() => Location.Store.Release();

    /// <summary>
    /// Deletes the checkpoint file once the worker ended or a stop abandoned it, when the automation is detached or the
    /// graph no longer holds it at the path of the checkpoint, so that the path resolves to what took its place.
    /// </summary>
    public void DeleteCheckpointIfOrphaned(bool isDetached)
    {
        if (isDetached || Location.HasMoved(_automation))
        {
            Location.DeleteIfOrphaned(_automation, _logger);
        }
    }

    /// <summary>
    /// Abandons a worker that did not end when the automation stopped: from now on it changes neither the automation, its
    /// rules nor the checkpoint file, and the rules show that the automation stopped.
    /// </summary>
    public void Abandon()
    {
        _engine.Abandon();
        Release();
    }

    /// <summary>Has the triggers that the next configuration subscribes handle the events received from now on; called before the configuration version changes.</summary>
    public void MarkConfigurationRequested() => _queue.MarkConfigurationRequested();

    public void RequestConfiguration() => _queue.TryEnqueue(ConfigurationChangedInput.Instance);

    /// <summary>The receiver of the property changes of the automation.</summary>
    internal AutomationChangeReceiver Changes => _changes;

    // White-box test hook: the engine of the rules.
    internal AutomationEngine Engine => _engine;

    /// <summary>Queues a reset; false when the queue is full or closed, or the reset continues a feedback chain beyond the limit.</summary>
    public bool RequestReset()
    {
        return _queue.TryEnqueue(new ResetStateInput(AutomationInputQueue.CausedChainDepth));
    }

    /// <summary>
    /// Completes once the worker processed every input queued so far and set its timer for them; the worker changes nothing
    /// after it completed a flush until it takes the next input.
    /// </summary>
    public Task FlushAsync()
    {
        var flush = FlushInput.Create();
        return _queue.EnqueueFlush(flush) ? flush.Completion.Task : Task.CompletedTask;
    }

    /// <summary>Refuses later inputs and completes the flushes still queued, since nothing is processed anymore.</summary>
    public void Close()
    {
        foreach (var input in _queue.Close())
        {
            if (input is FlushInput flush)
            {
                flush.Completion.TrySetResult();
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var context = ((IInterceptorSubject)_automation).Context;
        using var registration = _changes.Register(_automation.ChangeDispatcher, context);
        try
        {
            _engine.Start();

            // A move between the start and the registration queued no graph change.
            FollowPath();
            _routes.Publish();
            _engine.RefreshRuleStatuses();
            using var wakeTimer = new WakeTimer(_automation.TimeProvider, EnqueueTick);
            _wakeTimer = wakeTimer;
            ScheduleWake();
            while (!cancellationToken.IsCancellationRequested)
            {
                var input = await _queue.ReadAsync(cancellationToken);
                if (input is FlushInput flush)
                {
                    CompleteOrRequeue(flush);
                    continue;
                }

                await HandleAsync(input, cancellationToken);
                ScheduleWake();
            }
        }
        finally
        {
            try
            {
                _engine.End();
            }
            finally
            {
                Close();
            }
        }
    }

    private async Task HandleAsync(AutomationInput input, CancellationToken cancellationToken)
    {
        ClearPendingFlag(input);
        using var run = AutomationInputQueue.EnterRun(input.ChainDepth);
        try
        {
            _engine.ApplyConfigurationIfChanged();
            await RouteAsync(input, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _engine.ReportUnexpectedError(exception);
        }

        _routes.Publish();
        try
        {
            _engine.RefreshRuleStatuses();
        }
        catch (Exception exception)
        {
            _engine.ReportUnexpectedError(exception);
        }
    }

    private Task RouteAsync(AutomationInput input, CancellationToken cancellationToken)
    {
        switch (input)
        {
            case ResynchronizeInput resynchronize:
                return ResynchronizeAsync(resynchronize, cancellationToken);
            case ResetStateInput:
                _engine.ResetState();
                return Task.CompletedTask;
            case GraphChangedInput:
                FollowPath();
                break;
        }

        return _engine.HandleAsync(input, cancellationToken);
    }

    // The runs that make up for inputs the full queue dropped continue the chain of those inputs, so that an overload does
    // not restart a chain at depth 0.
    private async Task ResynchronizeAsync(ResynchronizeInput input, CancellationToken cancellationToken)
    {
        var dropped = _queue.TakeDroppedInputs(input);
        if (dropped.HasGraphChange)
        {
            FollowPath();
        }

        using var run = AutomationInputQueue.EnterRun(dropped.OverloadDepth);
        await _engine.ResynchronizeAsync(dropped, cancellationToken);
    }

    // The checkpoint belongs to the path: an automation that moved starts again as a new automation at the new path, and
    // the file at the old path is deleted. Checked on graph changes, which are queued after the write that moved it, not on
    // its own lifecycle changes, which come before that write took effect. A move adds to the new parent before it removes
    // from the old one, so a check between the two writes may still see the old path; the coalesced graph change
    // guarantees a check after the second.
    private void FollowPath()
    {
        var hasMoved = Location.HasMoved(_automation, _routes.BeginRoute());
        _routes.EndRoute(_routes.Location);
        if (!hasMoved)
        {
            return;
        }

        var previous = Location;
        Location = CheckpointLocation.Of(_automation, _logger);
        _keeper.UseStore(Location.Store);

        // Without a path at the start there was no checkpoint to keep, so the first path is where the automation starts.
        if (previous.AutomationPath is null)
        {
            _engine.StartFromCheckpoint();
            return;
        }

        _engine.StartFresh();
        previous.DeleteIfOrphaned(_automation, _logger);
        previous.Store.Release();
    }

    private void CompleteOrRequeue(FlushInput flush)
    {
        if (_queue.HasWork && _queue.EnqueueFlush(flush))
        {
            return;
        }

        flush.Completion.TrySetResult();
    }

    private void ClearPendingFlag(AutomationInput input)
    {
        // A full fence, as for the graph change: with a plain store, a timer that fires while the store is still buffered
        // finds the flag set and queues no tick, while this tick reads the clock from before the instant it was due.
        if (input is TickInput)
        {
            Interlocked.Exchange(ref _isTickPending, 0);
        }
        else if (input is GraphChangedInput)
        {
            _routes.BeginResolving();
            _changes.OnGraphChangeTaken();
        }
    }

    private void EnqueueTick()
    {
        if (Interlocked.Exchange(ref _isTickPending, 1) == 0 && !_queue.TryEnqueue(TickInput.Instance))
        {
            Volatile.Write(ref _isTickPending, 0);
        }
    }

    private void ScheduleWake()
    {
        var now = _automation.TimeProvider.GetUtcNow();
        _wakeTimer?.Schedule(_engine.GetNextDueAt(now), now);
    }
}

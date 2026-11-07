using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The worker of one automation. Callbacks on writing threads only enqueue: a change of a watched property and a graph
/// change (coalesced), both routed by <see cref="AutomationChangeDispatcher"/>, and a tick (coalesced) from
/// <see cref="WakeTimer"/>, which the worker sets after every input to when the engine is due next and stops while nothing
/// is due. A graph change is routed only for a property on the graph route that the worker publishes after every input
/// that resolved paths, see <see cref="GraphRoutes"/>, or when the automation or one of its rules gains a parent. The
/// worker processes one input at a time with <see cref="AutomationEngine"/>, so no rule runs inside a property setter.
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
    private CheckpointLocation _location;
    private int _isTickPending;
    private WakeTimer? _wakeTimer;

    /// <summary>Creates the worker of the automation, whose checkpoint is at <paramref name="location"/>.</summary>
    public AutomationRuntime(Automation automation, CheckpointLocation location, ILogger logger)
    {
        _automation = automation;
        _logger = logger;
        _location = location;
        _keeper = new AutomationStateKeeper(location.Store);
        _changes = new AutomationChangeReceiver(_queue);
        _engine = new AutomationEngine(automation, _queue, _keeper, logger, _changes);
    }

    // White-box test hooks: the engine and the change receiver of the worker.
    internal AutomationEngine Engine => _engine;

    internal AutomationChangeReceiver Changes => _changes;

    /// <summary>The checkpoint of the automation: where it started, or where it moved to later. Changed by the worker only.</summary>
    public CheckpointLocation Location
    {
        get => Volatile.Read(ref _location);
        private set => Volatile.Write(ref _location, value);
    }

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
    /// Abandons a worker that did not end when the automation stopped: from now on it changes neither the automation, its
    /// rules nor the checkpoint file, and the rules show that the automation stopped.
    /// </summary>
    public void Abandon()
    {
        _engine.Abandon();
        Release();
    }

    public void RequestConfiguration() => _queue.TryEnqueue(ConfigurationChangedInput.Instance);

    /// <summary>
    /// Queues a graph change, since the automation or one of its rules gained a parent through a property that is not on
    /// its graph route. Called on the writing thread.
    /// </summary>
    public void OnParentAdded() => _changes.RequestGraphChange(AutomationInputQueue.CausedChainDepth);

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
            _engine.PublishRoutes(0);
            _engine.RefreshRuleStatuses();
            using var wakeTimer = new WakeTimer(_automation.TimeProvider, () => EnqueueOnce(ref _isTickPending, TickInput.Instance));
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

        _engine.PublishRoutes(input.ChainDepth);
        _engine.RefreshRuleStatuses();
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
            case GraphChangedInput when _engine.MayHaveMoved:
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

    // The checkpoint belongs to the path, so an automation that moved, such as a file moved to another folder, starts
    // again as a new automation at the new path, and the file at the old path is deleted. Checked on graph changes: the
    // write that removes the automation from a parent or changes a parent of an ancestor has a property on the route of
    // the location, and a parent that the automation gains queues one while the write that adds it runs. A move adds the
    // automation to its new parent before it removes it from the old one: a check between the two writes may still see
    // the old path, and a check after the second, which the coalesced graph change guarantees, sees the new one; once
    // moved, further checks find nothing to do.
    private void FollowPath()
    {
        _engine.RecordLocation();
        if (!Location.HasMoved(_automation))
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
        if (input is TickInput)
        {
            Volatile.Write(ref _isTickPending, 0);
        }
        else if (input is GraphChangedInput)
        {
            _engine.TakeGraphChanges();
        }
    }

    private void EnqueueOnce(ref int isPending, AutomationInput input)
    {
        if (Interlocked.Exchange(ref isPending, 1) == 0 && !_queue.TryEnqueue(input))
        {
            Volatile.Write(ref isPending, 0);
        }
    }

    private void ScheduleWake()
    {
        var now = _automation.TimeProvider.GetUtcNow();
        _wakeTimer?.Schedule(_engine.GetNextDueAt(now), now);
    }
}

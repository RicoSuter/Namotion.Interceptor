using System.Reactive.Concurrency;
using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The worker of one automation. Callbacks on writing threads only enqueue: a change of a watched property, a change of
/// a property that holds subjects (coalesced), and a tick every wall-clock second (coalesced). The worker processes one
/// input at a time with <see cref="AutomationEngine"/>, so no rule runs inside a property setter.
/// </summary>
internal sealed class AutomationRuntime
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly Automation _automation;
    private readonly AutomationInputQueue _queue = new();
    private readonly AutomationEngine _engine;
    private int _isTickPending;
    private int _isGraphChangePending;

    /// <summary>Creates the worker of the automation at <paramref name="automationPath"/>, whose checkpoint is in <paramref name="store"/>.</summary>
    public AutomationRuntime(Automation automation, string? automationPath, AutomationCheckpointStore store, ILogger logger)
    {
        _automation = automation;
        AutomationPath = automationPath;
        Store = store;
        _engine = new AutomationEngine(automation, _queue, new AutomationStateKeeper(store), logger);
    }

    /// <summary>The canonical path of the automation when it started, which names its checkpoint.</summary>
    public string? AutomationPath { get; }

    public AutomationCheckpointStore Store { get; }

    public void RequestConfiguration() => _queue.TryEnqueue(ConfigurationChangedInput.Instance);

    /// <summary>Queues a reset; false when the queue is full or closed, or the reset continues a feedback chain beyond the limit.</summary>
    public bool RequestReset()
    {
        var chainDepth = AutomationInputQueue.CausedChainDepth;
        return _queue.TryEnqueue(chainDepth == 0 ? ResetStateInput.Instance : new ResetStateInput(chainDepth));
    }

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
        using var changes = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(OnPropertyChanged);
        _engine.Start();
        _engine.RefreshRuleStatuses();
        using var ticker = StartTicker();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var input = await _queue.ReadAsync(cancellationToken);
                await HandleAsync(input, cancellationToken);
            }
        }
        finally
        {
            try
            {
                _engine.Stop();
            }
            finally
            {
                Close();
            }
        }
    }

    private async Task HandleAsync(AutomationInput input, CancellationToken cancellationToken)
    {
        if (input is FlushInput flush)
        {
            CompleteOrRequeue(flush);
            return;
        }

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

        _engine.RefreshRuleStatuses();
    }

    private Task RouteAsync(AutomationInput input, CancellationToken cancellationToken) => input switch
    {
        PropertyChangedInput change => _engine.OnPropertyChangedAsync(change, cancellationToken),
        GraphChangedInput => _engine.OnGraphChangedAsync(cancellationToken),
        EventInput subjectEvent => _engine.OnEventAsync(subjectEvent, cancellationToken),
        TickInput => _engine.OnTickAsync(cancellationToken),
        ActivationInput activation => _engine.OnActivationAsync(activation, cancellationToken),
        ResynchronizeInput resynchronize => ResynchronizeAsync(resynchronize, cancellationToken),
        ResetStateInput => _engine.ResetStateAsync(),
        _ => Task.CompletedTask
    };

    // The runs that make up for inputs the full queue dropped continue the chain of those inputs, so that an overload does
    // not restart a chain at depth 0.
    private async Task ResynchronizeAsync(ResynchronizeInput input, CancellationToken cancellationToken)
    {
        var dropped = _queue.TakeDroppedInputs(input);
        using var run = AutomationInputQueue.EnterRun(dropped.OverloadDepth);
        await _engine.ResynchronizeAsync(dropped, cancellationToken);
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
            Volatile.Write(ref _isGraphChangePending, 0);
        }
    }

    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        if (_engine.IsWatched(change.Property))
        {
            _queue.TryEnqueue(new PropertyChangedInput(
                change.Property, change.GetNewValue<object?>(), change.Revision, AutomationInputQueue.CausedChainDepth));
        }
        else if (change.Property.TryGetRegisteredProperty() is { CanContainSubjects: true })
        {
            EnqueueOnce(ref _isGraphChangePending, new GraphChangedInput(AutomationInputQueue.CausedChainDepth));
        }
    }

    private void EnqueueOnce(ref int isPending, AutomationInput input)
    {
        if (Interlocked.Exchange(ref isPending, 1) == 0 && !_queue.TryEnqueue(input))
        {
            Volatile.Write(ref isPending, 0);
        }
    }

    private ITimer StartTicker()
    {
        var timeProvider = _automation.Services.TimeProvider;
        var pastSecond = timeProvider.GetUtcNow().UtcTicks % TimeSpan.TicksPerSecond;
        var untilNextSecond = TimeSpan.FromTicks(TimeSpan.TicksPerSecond - pastSecond);
        return timeProvider.CreateTimer(_ => EnqueueOnce(ref _isTickPending, TickInput.Instance), null, untilNextSecond, TickInterval);
    }
}

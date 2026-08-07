using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> load initial state -> run change queue processor -> retry on failure).
/// Derived classes override three hooks to plug in protocol-specific behavior:
/// <see cref="StartListeningAsync"/> (protected), <see cref="LoadInitialStateAsync"/> (public),
/// and <see cref="WriteChangesAsync"/> (public).
/// </summary>
public abstract class SubjectSourceBase : BackgroundService, ISubjectSource
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    private readonly Lock _stateLock = new();
    private int _state = (int)SourceState.Connecting;
    private long _lastSynchronizedTicks;
    private int _started;

    private ImmutableArray<SourceMonitor> _registeredMonitors = [];

    internal WriteRetryQueue? WriteRetryQueue { get; }

    /// <summary>
    /// Gets the number of writes currently queued for retry.
    /// </summary>
    public int PendingWriteCount => WriteRetryQueue?.PendingWriteCount ?? 0;

    /// <inheritdoc />
    public SourceState State => (SourceState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public DateTimeOffset? LastSynchronizedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSynchronizedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public event EventHandler<SourceEvent>? StateChanged;

    /// <summary>
    /// Reports that the connection was lost, for connectors that detect an outage before they
    /// start buffering.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="SubjectPropertyWriter.StartBuffering"/>: calling that
    /// at detection time would replace the buffer with a fresh list, and the later StartBuffering
    /// on the reconnect path would then discard everything buffered in between. Protected rather
    /// than public: application code holding an ISubjectSource reference must not be able to flip a
    /// synchronized source back to Connecting. A concrete source in another assembly that needs to
    /// call this from a helper object outside its own inheritance hierarchy (SessionManager for
    /// OpcUaSubjectClientSource) needs an internal forwarder on that source; see
    /// OpcUaSubjectClientSource for the pattern.
    /// <para>
    /// Also invalidates the property writer's generation (see
    /// <see cref="SubjectPropertyWriter.InvalidateGeneration"/>): an initial load already in flight
    /// when the connection drops must not apply the pre-outage snapshot it eventually returns, or
    /// certify it as Synchronized. Without this, that stale report would stand until the reconnect's
    /// own StartBuffering runs - the whole tail of the in-flight load, not a narrow race.
    /// </para>
    /// </remarks>
    protected void ReportConnectionLost()
    {
        _propertyWriter.InvalidateGeneration();
        TransitionTo(SourceState.Connecting);
    }

    /// <summary>
    /// Moves to <paramref name="newState"/> and publishes the change, or does nothing when the
    /// transition is a no-op or the source has already stopped.
    /// </summary>
    /// <remarks>
    /// The state write, timestamp write and event raise are all inside one lock: a bare
    /// compare-exchange is not enough, since a writer could set Synchronized, be preempted, let
    /// disposal set Stopped and unregister, then resume and publish Synchronized after Stopped -
    /// both compare-exchanges would have succeeded, so no stickiness rule could prevent it.
    /// </remarks>
    internal void TransitionTo(SourceState newState)
    {
        lock (_stateLock)
        {
            var oldState = (SourceState)_state;
            if (oldState == newState || oldState == SourceState.Stopped)
            {
                return;
            }

            _state = (int)newState;

            var now = DateTimeOffset.UtcNow;
            if (newState == SourceState.Synchronized)
            {
                Interlocked.Exchange(ref _lastSynchronizedTicks, now.UtcTicks);
            }

            var handlers = StateChanged;
            if (handlers is not null)
            {
                var sourceEvent = new SourceEvent(
                    SourceEventKind.StateChanged, this, null, oldState, newState, now);

                foreach (var handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<SourceEvent>)handler)(this, sourceEvent);
                    }
                    catch (Exception exception)
                    {
                        // A buggy handler must not be mistaken for a source failure, and must not
                        // prevent the remaining subscribers from observing the transition.
                        _logger.LogError(exception, "A StateChanged handler threw and was ignored.");
                    }
                }
            }
        }
    }

    protected SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000)
    {
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);

        if (writeRetryQueueSize > 0)
        {
            WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(this, logger);
    }

    /// <inheritdoc cref="ISubjectConnector.RootSubject" />
    public abstract IInterceptorSubject RootSubject { get; }

    /// <inheritdoc cref="ISubjectSource.WriteBatchSize" />
    public virtual int WriteBatchSize => 0;

    /// <summary>
    /// Initializes the source and starts listening for external changes.
    /// </summary>
    /// <param name="propertyWriter">The writer to use for applying inbound property updates to the subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// An async disposable that can be used to stop listening for changes,
    /// or <c>null</c> if there is nothing to dispose.
    /// </returns>
    protected abstract Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Stopped is terminal, but the platform won't enforce it: BackgroundService.StartAsync
        // creates a fresh CancellationTokenSource each call, so a second StartAsync would run
        // ExecuteAsync again against an uncancelled token. Without this guard, a "restarted" source
        // would claim, load and apply live values while State stayed Stopped.
        if (State == SourceState.Stopped)
        {
            _logger.LogWarning(
                "Source {Source} was stopped and cannot be restarted. Create a new instance instead.",
                GetType().Name);
            return Task.CompletedTask;
        }

        // A source registered in DI AND attached to the subject graph is started down both paths.
        // Without this latch both run a pump: the first to exit latches Stopped in its finally while
        // the second is still applying live values.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogWarning(
                "Source {Source} is already started and the duplicate start was ignored. It is most " +
                "likely both registered in DI and attached to the subject graph; use one or the other.",
                GetType().Name);
            return Task.CompletedTask;
        }

        // Registration precedes the pump so SourceRegistered precedes any StateChanged of this source.
        var monitors = RootSubject.Context.GetSourceMonitors();
        ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, monitors);
        try
        {
            foreach (var monitor in monitors)
            {
                monitor.Register(this);
            }
        }
        catch
        {
            // A half-registered source that never pumps hangs every in-scope wait, which is worse
            // than not being monitored at all. Unwind and let the failure propagate.
            ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
            foreach (var monitor in monitors)
            {
                monitor.Unregister(this);
            }

            throw;
        }

        // Dispose can race this method: it may run (and find nothing yet in _registeredMonitors to
        // unregister) between the guard above and the assignment/registration loop just completed.
        // TransitionTo is monotonic and Stopped is terminal, so if State reads Stopped here, Dispose
        // has already run - re-check and unwind whatever was just registered so a disposed source
        // never stays registered forever.
        //
        // Unwinds through the LOCAL monitors array, never by re-reading the field: a Dispose that
        // interleaved between the assignment and the loop above has already taken the field and left
        // it empty, so re-reading here would strand this call's own registrations forever.
        // Unregister is a no-op for an unregistered source, so a double unwind is harmless.
        if (State == SourceState.Stopped)
        {
            ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
            foreach (var monitor in monitors)
            {
                monitor.Unregister(this);
            }

            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _propertyWriter.StartBuffering();
                    await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                    await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                    using var processor = new ChangeQueueProcessor(
                        this,
                        _context,
                        propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                        WriteChangesViaRetryQueueAsync,
                        _bufferTime,
                        maxQueueDepth: null,
                        logger: _logger);

                    // Optimistic retry re-apply: after initial state load + ChangeQueueProcessor creation,
                    // re-apply queued changes locally if the source hasn't changed the property.
                    // ChangeQueueProcessor picks up re-applied changes and sends them to the source as fresh writes.
                    ReapplyRetryQueue();

                    await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    TransitionTo(SourceState.Connecting);
                    _logger.LogError(ex, "Failed to listen for changes in source.");
                    try
                    {
                        await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            TransitionTo(SourceState.Stopped);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteChangesViaRetryQueueAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (WriteRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                var result = await this.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
                if (!result.IsFullySuccessful)
                {
                    _logger.LogError(result.Error, "Failed to write {Count} changes to source.",
                        result.FailedChanges.Length);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to write changes to source.");
            }
            return;
        }

        // First flush any queued changes
        var succeeded = await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            WriteRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            var result = await this.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            if (!result.IsFullySuccessful)
            {
                _logger.LogWarning(result.Error, "Failed to write {Count} changes to source, queuing for retry.",
                    result.FailedChanges.Length);
                WriteRetryQueue.Enqueue(result.FailedChanges.AsMemory());
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to source, queuing for retry.", changes.Length);
            WriteRetryQueue.Enqueue(changes);
        }
    }

    private void ReapplyRetryQueue()
    {
        var retryChanges = WriteRetryQueue?.DrainForLocalReapply();
        if (retryChanges is null || retryChanges.Length == 0)
        {
            return;
        }

        var applied = 0;
        var dropped = 0;
        var failed = 0;
        foreach (var change in retryChanges)
        {
            try
            {
                var property = change.Property;
                var currentValue = change.GetCurrentValue<object>();
                var oldValue = change.GetOldValue<object>();

                if (Equals(currentValue, oldValue))
                {
                    // Server hasn't changed this property - re-apply client's change locally.
                    // The interceptor chain fires, ChangeQueueProcessor captures the change, and sends it to the source.
                    property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
                    applied++;
                }
                else
                {
                    dropped++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to re-apply retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
            }
        }

        if (dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue optimistic re-apply: {Applied} re-applied, {Dropped} dropped (source wins), {Failed} failed.",
                applied, dropped, failed);
        }
        else if (applied > 0)
        {
            _logger.LogInformation(
                "Retry queue optimistic re-apply: {Applied} changes re-applied.",
                applied);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Publish the final Stopped while still registered, so a dispose without a stop is not silent.
        TransitionTo(SourceState.Stopped);

        // Take-and-clear in one step, so a concurrent StartAsync unwinding through its own local
        // array (see StartAsync) cannot have this method unregister the same entries a second time
        // on a later call, and so the field is never read while another thread is writing it.
        var monitors = ImmutableInterlocked.InterlockedExchange(
            ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
        foreach (var monitor in monitors)
        {
            monitor.Unregister(this);
        }

        WriteRetryQueue?.Dispose();
        base.Dispose();
    }
}

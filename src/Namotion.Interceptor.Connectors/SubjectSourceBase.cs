using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> load initial state -> run change queue processor, resynchronizing after every load -> retry on failure).
/// Derived classes override three hooks to plug in protocol-specific behavior:
/// <see cref="StartListeningAsync"/> (protected), <see cref="LoadInitialStateAsync"/> (public),
/// and <see cref="WriteChangesAsync"/> (public).
/// </summary>
public abstract class SubjectSourceBase : SubjectConnectorBase, ISubjectSource
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;

    // A source we talk to over a wire: what it hands us was produced before it saw our write, so it
    // cannot rank against our commits. Named once because the processor and the reconcile
    // must agree; if only one ranked against the last commit, the other would still deliver an older one.
    private const ChangeDeliveryRule DeliveryRule = ChangeDeliveryRule.SourceValuesMayBeStale;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    private readonly Lock _stateLock = new();

    // The state and its two timestamps are swapped as one value: in separate fields a reader can see
    // the new state beside the previous timestamp, and LastSynchronizedAt has to be visible before
    // State becomes Stopped.
    private sealed record SourceStateSnapshot(
        SourceState State, DateTimeOffset ChangeTime, DateTimeOffset? LastSynchronizedAt);

    private SourceStateSnapshot _stateSnapshot = new(SourceState.Synchronizing, DateTimeOffset.UtcNow, null);
    private int _started;

    private ImmutableArray<SourceMonitor> _registeredMonitors = [];

    // Property writer generations (see SubjectPropertyWriter): the latest whose load completed, and the
    // latest the pump has resynchronized after. While the writer's buffering generation is newer than
    // the latter, the processor parks writes instead of sending them, so nothing reaches the source
    // unreconciled. _loadedGeneration and _runCancellation change under _resyncLock; the pump alone
    // writes _resyncedGeneration.
    private readonly Lock _resyncLock = new();
    private int _loadedGeneration;
    private int _resyncedGeneration;
    private CancellationTokenSource? _runCancellation;

    // With no capacity there is nothing to park into, so writes during a load are sent as before.
    private readonly bool _parksWrites;

    internal WriteRetryQueue WriteRetryQueue { get; }

    protected SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000,
        ThroughputCounter? incomingThroughput = null,
        ThroughputCounter? outgoingThroughput = null)
        : this(context, logger, bufferTime, retryTime, writeRetryQueueSize,
            new SourceMetrics(incomingThroughput, outgoingThroughput))
    {
    }

    // A constructor initializer cannot reference this, so the metrics instance is threaded through
    // here to reach both base(...) and the narrowed Metrics property as the same object.
    private SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime,
        TimeSpan? retryTime,
        int writeRetryQueueSize,
        SourceMetrics metrics)
        : base(metrics)
    {
        Metrics = metrics;
        Diagnostics = new SourceDiagnostics(metrics);

        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfNegative(writeRetryQueueSize);

        WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger, metrics.OutboundRetries);
        _parksWrites = writeRetryQueueSize > 0;

        // The registration lives as long as the source, and the queue count stays readable after
        // the queue itself is disposed.
        _ = metrics.OutboundRetries.Register(
            () => WriteRetryQueue.PendingWriteCount, capacity: writeRetryQueueSize);

        _propertyWriter = new SubjectPropertyWriter(this, logger, metrics.InboundBuffer);
    }

    /// <summary>
    /// Gets the write side of this source's diagnostics, narrowed to <see cref="SourceMetrics"/>.
    /// </summary>
    /// <remarks>
    /// A derived source must not register on <see cref="Diagnostics.ConnectorMetrics.OutboundChanges"/>,
    /// <see cref="SourceMetrics.OutboundRetries"/> or <see cref="SourceMetrics.InboundBuffer"/>: this
    /// base owns all three, and a second live registration makes
    /// <see cref="Diagnostics.QueueMetrics.Register"/> throw.
    /// </remarks>
    protected new SourceMetrics Metrics { get; }

    /// <summary>
    /// Gets what this source reports about its transport and its buffers.
    /// </summary>
    public override SourceDiagnostics Diagnostics { get; }

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
        // Stopped is terminal, but the base class won't enforce it: SubjectConnectorBase.StartAsync
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
            // Read before the transition below sets it, so Stopped still means Dispose, whose own
            // unwind may have found nothing registered yet.
            if (State == SourceState.Stopped)
            {
                UnwindRegistrations(monitors);
                throw;
            }

            // This source will never pump. Reporting a stop keeps it in scope as never-synchronized,
            // so in-scope waits answer Incomplete; unwinding left them on a vacuous Synchronized.
            // Nothing unregisters it until Dispose, which a graph-attached source may never get.
            WriteRetryQueue.Retire();
            TransitionStateTo(SourceState.Stopped);
            throw;
        }

        // Dispose can interleave with the registration above, and Stopped is terminal, so seeing it
        // here means Dispose already ran.
        if (State == SourceState.Stopped)
        {
            UnwindRegistrations(monitors);
            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Drops the registrations <see cref="StartAsync"/> just made, through its LOCAL array rather
    /// than the field, which a concurrent <see cref="Dispose"/> may already have emptied: re-reading
    /// it would strand them. Unregister no-ops on an unregistered source, so a double unwind is safe.
    /// </summary>
    private void UnwindRegistrations(ImmutableArray<SourceMonitor> monitors)
    {
        ImmutableInterlocked.InterlockedExchange(ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
        foreach (var monitor in monitors)
        {
            monitor.Unregister(this);
        }
    }

    /// <inheritdoc />
    protected sealed override async Task RunAsync(CancellationToken stoppingToken)
    {
        // Inside the try, so the finally below still publishes Stopped when startup fails. Outside it, a
        // configuration error leaves the source registered as Synchronizing for the process lifetime:
        // the DI path tears the host down, but on the graph-attach path the faulted task is swallowed and
        // every WaitForSynchronizationAsync on that branch blocks until its caller's token fires. A silent
        // hang in place of the loud failure the guard exists to give.
        try
        {
            // A missing PropertyChangeInterceptor means the source can capture no writes: a configuration
            // error, so fail fast with an actionable message instead of running silently inert. Detect it
            // precisely (null-check, not catch-all) so unrelated failures surface with their own diagnosis.
            if (_context.TryGetService<PropertyChangeInterceptor>() is null)
            {
                throw new InvalidOperationException(
                    "Cannot start source: no PropertyChangeInterceptor is registered in the interceptor context. " +
                    "Add WithPropertyChangeSubscriptions() or WithFullPropertyTracking() to the context configuration.");
            }

            // Source-lifetime capture: one subscription for the whole source, so writes are captured
            // continuously (including during the retry delay) and never fall into a no-subscription gap.
            using var subscription = _context.CreatePropertyChangeQueueSubscription();

            try
            {
                var firstAttempt = true;
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!firstAttempt)
                        {
                            await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
                        }
                        firstAttempt = false;

                        // No processor exists between attempts, so a connect that keeps failing would grow the
                        // subscription without bound: park the owned writes captured since the previous
                        // attempt into the bounded retry queue instead.
                        DrainOwnedWritesToRetryQueue(subscription);

                        // Opens a generation (see SubjectPropertyWriter). From here until the pump has
                        // resynchronized after that generation's load, the processor parks writes instead of
                        // sending them.
                        _propertyWriter.StartBuffering();
                        await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);
                        await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                        // Connected: the pump is the subscription's only consumer until the source stops or a
                        // processor faults. It starts after the load and not after the listen, because a
                        // connector may claim ownership as late as the load's apply (WebSocket) and the
                        // processor keeps only what this source owns at the time it dequeues.
                        await PumpAsync(subscription, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // The base class only sees exceptions that leave RunAsync, and this loop swallows
                        // every per-attempt failure, so a source that can never connect would otherwise
                        // report no error at all. Guarded because the clause above covers only the
                        // cancellation, while a stop tears the connection down mid-connect with an
                        // arbitrary exception: recording that would overwrite the genuine fault for good,
                        // since LastError is sticky and a stopped source never restarts.
                        if (!stoppingToken.IsCancellationRequested)
                        {
                            Metrics.ReportError(ex);
                        }

                        // Whatever it reported before the failure, the source is no longer serving the model.
                        TransitionStateTo(SourceState.Synchronizing);
                        _logger.LogError(ex, "Failed to listen for changes in source.");
                        // The next iteration delays before reconnecting, with the subscription still capturing.
                    }
                }
            }
            finally
            {
                // The attempt's pump has ended, so this is the only consumer. An owned write nothing consumed
                // is still counted: the queue is retired below, or already was by the processor, so what
                // lands here is reported as dropped rather than lost silently.
                DrainOwnedWritesToRetryQueue(subscription);
            }
        }
        finally
        {
            WriteRetryQueue.Retire();
            TransitionStateTo(SourceState.Stopped);
        }
    }

    /// <summary>
    /// Resynchronizes after the completed load, then runs processors on the source-lifetime subscription
    /// with a resynchronization after every further load, until the source stops or a processor faults.
    /// </summary>
    private async Task PumpAsync(PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        do
        {
            await ResynchronizeAsync(subscription, stoppingToken).ConfigureAwait(false);
        }
        while (!stoppingToken.IsCancellationRequested &&
               await RunProcessorAsync(subscription, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Parks the owned writes still in the subscription and reconciles the retry queue against the model
    /// the latest completed load produced. Runs only while no processor consumes the subscription.
    /// </summary>
    private async Task ResynchronizeAsync(PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        // Read before the drain: a load that completes after this point has not been judged here, so it
        // ends the next run straight away.
        var generation = Volatile.Read(ref _loadedGeneration);

        DrainOwnedWritesToRetryQueue(subscription);

        // Not when a newer StartBuffering has happened: the model this load produced is about to be
        // replaced by the next load, whose resynchronization judges the parked writes against what it
        // produced.
        if (_propertyWriter.BufferingGeneration <= generation)
        {
            await ReconcileWithinStopBoundAsync(stoppingToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _resyncedGeneration, generation);
    }

    /// <summary>
    /// Single reconcile point: send (model already holds it), restore (the load moved the model off it),
    /// drop (a later local write supersedes it). Abandoned <see cref="ChangeQueueProcessor.TeardownFlushBound"/>
    /// after the stop, so a transport write that ignores cancellation cannot hold the stop forever.
    /// </summary>
    private async Task ReconcileWithinStopBoundAsync(CancellationToken stoppingToken)
    {
        var reconcile = ReconcileRetryQueueAsync(stoppingToken);
        try
        {
            await reconcile.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!reconcile.IsCompleted)
        {
            try
            {
                await reconcile.WaitAsync(ChangeQueueProcessor.TeardownFlushBound).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The drain has run, so nothing else consumes the subscription while the reconcile finishes
                // on its own, and Retire settles whatever its late write still owns.
                _ = reconcile.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _logger.LogWarning(
                    "Gave up waiting after {Timeout} for the retry queue reconcile to finish while stopping.",
                    ChangeQueueProcessor.TeardownFlushBound);
            }
        }
    }

    /// <summary>
    /// Runs a change queue processor on the source-lifetime subscription, which it does not own, until
    /// the source stops or a completed load requests a resynchronization.
    /// </summary>
    /// <returns><c>true</c> when a resynchronization is due rather than the stop.</returns>
    private async Task<bool> RunProcessorAsync(
        PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_resyncLock)
        {
            // A load completed during the resynchronization.
            if (IsResynchronizationDue)
            {
                return true;
            }

            _runCancellation = runCancellation;
        }

        using (var processor = new ChangeQueueProcessor(
            this,
            subscription,
            propertyReference => propertyReference.TryGetSource(out var source) && source == this,
            (changes, token) => DeliverFromProcessorAsync(changes, token, stoppingToken),
            DeliveryRule,
            _bufferTime,
            maxQueueDepth: null,
            logger: _logger,
            dropHandler: Metrics.OutboundChanges.CreateDropReporter(),
            writeHandlerOwnsChanges: true,
            terminalHandler: () =>
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    WriteRetryQueue.Retire();
                }
            },
            completionHandler: async teardownToken =>
            {
                if (!IsResynchronizationPending)
                {
                    await WriteRetryQueue.FlushAsync(this, teardownToken).ConfigureAwait(false);
                }
            },
            // A run that ends for a load parks its final flush, so all it can wait on is the one write that
            // was in flight when the load completed; the teardown bound applies to the stop alone.
            stoppingToken: stoppingToken))
        {
            // Declared after the processor so it is released first, which is what lets the next
            // run or retry attempt register its own: a second Register while one is still live
            // throws. Drops are reported into the lifetime-owned metrics directly, so releasing this
            // depth provider cannot lose them.
            using var outboundRegistration = Metrics.OutboundChanges.Register(
                () => processor.QueueDepth, capacity: null);

            try
            {
                await processor.ProcessAsync(runCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_resyncLock)
                {
                    _runCancellation = null;
                }
            }
        }

        return !stoppingToken.IsCancellationRequested;
    }

    private bool IsResynchronizationPending =>
        _propertyWriter.BufferingGeneration > Volatile.Read(ref _resyncedGeneration);

    // Under _resyncLock. A source that cannot park sends through a reload as it did before loads were
    // resynchronized: ending its run would hand what the processor dequeued meanwhile to a drain that
    // cannot retain it.
    private bool IsResynchronizationDue => _parksWrites && _loadedGeneration > _resyncedGeneration;

    private ValueTask DeliverFromProcessorAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken processorToken, CancellationToken stoppingToken)
    {
        if (_parksWrites && IsResynchronizationPending)
        {
            // Sending would flush the parked backlog ahead of the reconcile that has to judge it, so the
            // processor parks instead, its final flush included.
            WriteRetryQueue.Enqueue(changes);
            return ValueTask.CompletedTask;
        }

        // A live send is cancelled by the stop alone, not by the end of a run: a load that completes while
        // a write is in flight waits for it, so the reconcile cannot resend what the source has accepted.
        // Once the stop is requested the processor's own token applies, which keeps the final flush
        // within the teardown bound.
        var sendToken = stoppingToken.IsCancellationRequested ? processorToken : stoppingToken;
        return WriteRetryQueue.WriteAsync(this, changes, sendToken);
    }

    /// <summary>
    /// Called by the property writer after a load for <paramref name="generation"/> applied and was not
    /// superseded. Ends the running processor so the pump resynchronizes against the reloaded model.
    /// </summary>
    internal void OnInitialStateLoaded(int generation)
    {
        lock (_resyncLock)
        {
            if (generation <= _loadedGeneration)
            {
                return;
            }

            Volatile.Write(ref _loadedGeneration, generation);

            // Under the lock so the pump cannot dispose the source in between. Nothing registers a
            // callback on this token (ProcessAsync waits on its handle), so no foreign code runs here.
            if (IsResynchronizationDue)
            {
                _runCancellation?.Cancel();
            }
        }
    }

    internal void DrainOwnedWritesToRetryQueue(PropertyChangeQueueSubscription subscription)
    {
        List<SubjectPropertyChange>? owned = null;
        while (subscription.TryDequeueImmediate(out var change))
        {
            if (ReferenceEquals(change.Origin.Source, this) && !ChangeDeliveryFilter.NeedsWriteBack(in change))
            {
                // This source's own applies (inbound / source-tagged). The exception is a transaction
                // confirmation on a property a connector has written out, which has to reach the source
                // to repair it; skipping it here would discard the repair for the whole connect window.
                continue;
            }

            if (!(change.Property.TryGetSource(out var source) && source == this))
            {
                continue; // not owned by this source
            }

            (owned ??= []).Add(change);
        }

        if (owned is not null)
        {
            // Collapsed before parking: a drain can hand the queue thousands of changes to a few
            // properties, and the queue collapses only once it has overflowed.
            WriteRetryQueue.Enqueue(CollapsePerProperty(owned.ToArray()).ToArray());
        }
    }

    /// <summary>
    /// Collapses parked changes to one per property with <see cref="WriteRetryQueue.Collapse"/>.
    /// </summary>
    /// <remarks>
    /// Reconciliation classifies each change against the live value and mutates that value when it
    /// restores, so two writes to one property have to be judged as one. Left separate, an older
    /// write can match the live value, get restored, and thereby make the newer write look diverged,
    /// which drops it: the older write would win over the newer one.
    /// </remarks>
    private static List<SubjectPropertyChange> CollapsePerProperty(SubjectPropertyChange[] changes)
    {
        var collapsed = new List<SubjectPropertyChange>(changes.Length);
        var indices = new Dictionary<PropertyReference, int>(changes.Length, PropertyReference.Comparer);

        foreach (var change in changes)
        {
            if (!indices.TryGetValue(change.Property, out var index))
            {
                indices[change.Property] = collapsed.Count;
                collapsed.Add(change);
                continue;
            }

            collapsed[index] = WriteRetryQueue.Collapse(collapsed[index], change);
        }

        return collapsed;
    }

    internal async Task ReconcileRetryQueueAsync(CancellationToken cancellationToken)
    {
        var retryChanges = WriteRetryQueue.DrainForLocalReapply();
        if (retryChanges.Length == 0)
        {
            return;
        }

        var restored = 0;
        var sent = 0;
        // Counted apart from dropped and failed, because only those two are added to
        // OutboundRetries.TotalDropped.
        var superseded = 0;
        var dropped = 0;
        var failed = 0;
        List<SubjectPropertyChange>? toSend = null;

        foreach (var change in CollapsePerProperty(retryChanges))
        {
            try
            {
                var property = change.Property;

                if (!ChangeDeliveryFilter.IsCurrent(in change, DeliveryRule))
                {
                    // A later local commit supersedes it, and that commit's change is delivered in its
                    // place.
                    superseded++;
                    continue;
                }

                // Still the latest local intent, so it has to reach the source. Decided by commit order
                // rather than by comparing values: the load writes the source's value into the model
                // without advancing the marker, and a value comparison cannot tell that apart from a
                // newer local write, so it discarded live writes.
                var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
                if (Equals(currentValue, change.GetNewValue<object?>()))
                {
                    // Already the current model value: the source has not received it, so send it.
                    // Marked here because this path flushes the retry queue directly rather than going
                    // through the processor, and without the mark a later transaction confirmation on
                    // this property is not written back, which is the divergence that repair exists for.
                    // Set without the subject lock, which is safe only because the reconcile runs between
                    // processor runs, so no confirmation for this connector is judged concurrently.
                    property.MarkAsPublishedToSource();
                    (toSend ??= []).Add(change);
                    sent++;
                }
                else if (property.Metadata.SetValue is { } setValue)
                {
                    // The load moved the model off it: restore locally so the connected phase captures
                    // and sends the re-applied write.
                    setValue(property.Subject, change.GetNewValue<object?>());
                    restored++;
                }
                else
                {
                    // No setter, so there is nothing to restore and the change has already left the
                    // queue. Derived properties reach this: their recomputation commits as Local and is
                    // parked like any other write. Counted as dropped rather than reported as restored.
                    dropped++;
                    Metrics.OutboundRetries.AddDropped(1);
                    _logger.LogWarning(
                        "Cannot restore the queued write for property '{PropertyName}': it has no setter, so the change is dropped.",
                        property.Name);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to reconcile retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
                Metrics.OutboundRetries.AddDropped(1);
            }
        }

        if (toSend is not null)
        {
            WriteRetryQueue.Enqueue(toSend.ToArray());
            await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        }

        if (superseded > 0 || dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue reconcile: {Restored} restored over the loaded source value, {Sent} sent, {Superseded} superseded by a later local write, {Dropped} dropped because the property has no setter, {Failed} failed.",
                restored, sent, superseded, dropped, failed);
        }
        else if (restored > 0 || sent > 0)
        {
            _logger.LogInformation(
                "Retry queue reconcile: {Restored} restored, {Sent} sent.", restored, sent);
        }
    }

    // ---- Source monitoring surface ----

    /// <inheritdoc />
    public SourceState State => Volatile.Read(ref _stateSnapshot).State;

    /// <inheritdoc />
    public DateTimeOffset StateChangeTime => Volatile.Read(ref _stateSnapshot).ChangeTime;

    /// <inheritdoc />
    public DateTimeOffset? LastSynchronizedAt => Volatile.Read(ref _stateSnapshot).LastSynchronizedAt;

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
    /// synchronized source back to Synchronizing. A concrete source in another assembly that needs to
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
        TransitionStateTo(SourceState.Synchronizing);
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
    internal void TransitionStateTo(SourceState newState)
    {
        lock (_stateLock)
        {
            var current = _stateSnapshot;
            var oldState = current.State;
            if (oldState == newState || oldState == SourceState.Stopped)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // Never cleared, so it still answers whether a good period ever began.
            var lastSynchronizedAt = newState == SourceState.Synchronized ? now : current.LastSynchronizedAt;

            Volatile.Write(ref _stateSnapshot, new SourceStateSnapshot(newState, now, lastSynchronizedAt));

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

    /// <inheritdoc />
    public override void Dispose()
    {
        // Close outbound admission before publishing the final Stopped, so an observer blocked inside
        // the notification cannot still hand a write to the transport. Publishing while registered keeps
        // a dispose without a stop visible to the monitors.
        WriteRetryQueue.Retire();
        TransitionStateTo(SourceState.Stopped);

        // Take-and-clear in one step, so a concurrent StartAsync unwinding through its own local
        // array (see StartAsync) cannot have this method unregister the same entries a second time
        // on a later call, and so the field is never read while another thread is writing it.
        var monitors = ImmutableInterlocked.InterlockedExchange(
            ref _registeredMonitors, ImmutableArray<SourceMonitor>.Empty);
        foreach (var monitor in monitors)
        {
            monitor.Unregister(this);
        }

        WriteRetryQueue.Dispose();
        base.Dispose();
    }
}

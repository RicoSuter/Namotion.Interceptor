using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> run change queue processor -> load initial state -> resynchronize -> retry on failure).
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

    // Property writer generations (see SubjectPropertyWriter): the latest started, the latest whose load
    // completed, and the latest the pump has resynchronized after. While the first differs from the last,
    // the processor parks writes instead of sending them, so nothing reaches the source unreconciled.
    // _loadedGeneration, _resyncedGeneration and _cycleSource change under _resyncLock.
    private readonly Lock _resyncLock = new();
    private int _startedGeneration;
    private int _loadedGeneration;
    private int _resyncedGeneration;
    private CancellationTokenSource? _cycleSource;

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

            // The pump is the subscription's only consumer from the first successful listen on, across
            // every later load, until the source stops or a processor faults. It starts after the listen
            // and not before, because ownership is established in there (the OPC UA browse) and the
            // processor keeps only what this source owns at the time it dequeues.
            Task? pump = null;
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

                        if (pump is null)
                        {
                            // Nothing consumes the subscription yet, so a connect that keeps failing would grow
                            // it without bound: park the owned writes of the previous attempt into the retry
                            // queue instead. Not while the pump runs, which parks them itself and is the only
                            // consumer the subscription may have.
                            DrainOwnedWritesToRetryQueue(subscription);
                        }

                        // Every load is a generation (see SubjectPropertyWriter). From here until the pump has
                        // resynchronized after that generation's load, the processor parks writes instead of
                        // sending them.
                        _propertyWriter.StartBuffering();
                        await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                        pump ??= PumpAsync(subscription, stoppingToken);

                        await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                        // Connected: returns on stop, throws when a processor faults.
                        await pump.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A pump that ended is a faulted one (it only returns on stop), so the next attempt
                        // starts a fresh one after it has listened again.
                        if (pump is { IsCompleted: true })
                        {
                            pump = null;
                        }

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
                if (pump is not null)
                {
                    // Before the subscription is disposed under it, and before the drain below becomes
                    // its consumer. A fault has already been observed by the loop.
                    await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                // An owned write nothing consumed is still counted: the queue is retired below, or already
                // was by the processor, so what lands here is reported as dropped rather than lost silently.
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
    /// Runs processor cycles on the source-lifetime subscription, resynchronizing between them, until the
    /// source stops or a processor faults.
    /// </summary>
    private async Task PumpAsync(PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        while (await RunProcessorUntilCycleAsync(subscription, stoppingToken).ConfigureAwait(false))
        {
            await ResynchronizeAsync(subscription, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parks the owned writes still in the subscription and reconciles the retry queue against the model
    /// the latest completed load produced. Runs only while no processor consumes the subscription.
    /// </summary>
    private async Task ResynchronizeAsync(PropertyChangeQueueSubscription subscription, CancellationToken cancellationToken)
    {
        // Read before the drain: a load that completes after this point has not been judged here, so it
        // cycles the next processor straight away.
        var generation = Volatile.Read(ref _loadedGeneration);

        DrainOwnedWritesToRetryQueue(subscription);

        // Not when a newer generation has started: the model this load produced is about to be replaced
        // by the next load, whose resynchronization judges the parked writes against what it produced.
        if (Volatile.Read(ref _startedGeneration) == generation)
        {
            // Single reconcile point: send (model already holds it), restore (the load moved the
            // model off it), drop (a later local write supersedes it).
            await ReconcileRetryQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_resyncLock)
        {
            if (generation > _resyncedGeneration)
            {
                Volatile.Write(ref _resyncedGeneration, generation);
            }
        }
    }

    /// <summary>
    /// Runs a change queue processor on the source-lifetime subscription, which it does not own, until
    /// the source stops or a completed load requests a resynchronization.
    /// </summary>
    /// <returns><c>true</c> when the run ended for a resynchronization rather than for the stop.</returns>
    private async Task<bool> RunProcessorUntilCycleAsync(
        PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        using var cycleSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using (var processor = new ChangeQueueProcessor(
            this,
            subscription,
            propertyReference => propertyReference.TryGetSource(out var source) && source == this,
            // While a resynchronization is pending, sending would flush the parked backlog ahead of the
            // reconcile that has to judge it, so the processor parks instead, its final flush included.
            (changes, token) => IsResynchronizationPending
                ? ParkWrites(changes)
                : WriteRetryQueue.WriteAsync(this, changes, token),
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
            // A cycle's final flush parks, so the only thing it can wait on is the one write that was in
            // flight when the load completed; the teardown bound applies to the stop alone.
            stoppingToken: stoppingToken))
        {
            // Declared after the processor so it is released first, which is what lets the next
            // cycle or retry attempt register its own: a second Register while one is still live
            // throws. Drops are reported into the lifetime-owned metrics directly, so releasing this
            // depth provider cannot lose them.
            using var outboundRegistration = Metrics.OutboundChanges.Register(
                () => processor.QueueDepth, capacity: null);

            lock (_resyncLock)
            {
                _cycleSource = cycleSource;

                // A load that completed while no processor was published.
                if (_loadedGeneration > _resyncedGeneration)
                {
                    cycleSource.Cancel();
                }
            }

            try
            {
                await processor.ProcessAsync(cycleSource.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_resyncLock)
                {
                    _cycleSource = null;
                }
            }
        }

        return !stoppingToken.IsCancellationRequested;
    }

    private bool IsResynchronizationPending =>
        Volatile.Read(ref _startedGeneration) != Volatile.Read(ref _resyncedGeneration);

    private ValueTask ParkWrites(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        WriteRetryQueue.Enqueue(changes);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the property writer, under its lock, whenever its generation advances.
    /// </summary>
    internal void OnGenerationAdvanced(int generation) => Volatile.Write(ref _startedGeneration, generation);

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
            _cycleSource?.Cancel();
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
            // Collapsed before parking, not only at reconcile time. The queue is a bounded ring buffer
            // that drops its oldest entries, so parking raw changes lets a burst on one property evict
            // other properties' window writes before the reconcile ever sees them. Collapsing first
            // makes the space this costs proportional to the number of properties written rather than
            // to the number of writes.
            WriteRetryQueue.Enqueue(CollapsePerProperty(owned.ToArray()).ToArray());
        }
    }

    /// <summary>
    /// Collapses parked changes to one per property, keeping the oldest old value and the new value
    /// of the highest-revision commit.
    /// </summary>
    /// <remarks>
    /// Reconciliation classifies each change against the live value and mutates that value when it
    /// restores, so two writes to one property have to be judged as one. Left separate, an older
    /// write can match the live value, get restored, and thereby make the newer write look diverged,
    /// which drops it: the older write would win over the newer one.
    /// <para>
    /// Which one is newer is decided by <see cref="SubjectPropertyChange.Revision"/>, not by capture
    /// order. Changes are enqueued after their commit and outside the subject lock, so under
    /// concurrent writers arrival order is a race order. Both changes are writes to the same
    /// property and therefore to the same subject, so their revisions are comparable. A change
    /// carrying revision 0 was built outside a terminal write and orders against nothing, so
    /// capture order decides between those and the survivor carries no revision either, matching
    /// the flush-path collapse in <c>ChangeMerger</c> on unordered changes. The two still differ on which
    /// old value survives when every revision is ordered, which the delivery contract calls best effort.
    /// </para>
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

            var kept = collapsed[index];
            collapsed[index] = change.Revision == 0 || kept.Revision == 0
                // One of them orders against nothing, so capture order decides and the survivor carries
                // no revision either. Same rule as the flush-path collapse: keeping a revision here would
                // let the survivor be ranked against the property marker and dropped, on a comparison
                // against a value it was not ordered by.
                ? kept.MergeWithNewer(change).WithoutRevision()
                : change.Revision < kept.Revision
                    ? change.MergeWithNewer(kept)
                    : kept.MergeWithNewer(change);
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
                    // Set without the subject lock, which is safe only because the reconcile runs before
                    // the processor starts, so no confirmation for this connector is judged concurrently.
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

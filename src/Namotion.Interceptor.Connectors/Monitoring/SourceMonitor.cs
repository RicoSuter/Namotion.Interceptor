using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// The per-tree registry of sources, the source event stream, and the synchronization waits.
/// Added to the tree root context by WithSourceMonitoring.
/// </summary>
/// <remarks>
/// Must run after ParentTrackingHandler: OnWaitConditionChanged re-evaluates every pending wait via
/// SourceScope.IsInScope, which walks the parent set ParentTrackingHandler maintains for the same
/// lifecycle change, so that set must already reflect this change by the time this handler runs.
/// Running after ContextInheritanceHandler keeps the two context-tracking handlers adjacent in the
/// order; nothing here currently depends on the fallback context it maintains.
/// </remarks>
[RunsAfter(typeof(ContextInheritanceHandler), typeof(ParentTrackingHandler))]
public class SourceMonitor : ILifecycleHandler, IStartupCompletionDeferrer
{
    private readonly Lock _lock = new();
    private Func<ILogger?>? _loggerResolver;

    private ILogger? _logger;


    // Boxed in a single-element array so the reference can be read with Volatile.Read: ImmutableArray<T>
    // is a struct, which Volatile/Interlocked cannot target directly. Writes happen under _lock and
    // swap the box wholesale, so Sources, HasSubscribers and Publish can read the latest snapshot
    // lock-free. Same technique as ParentsHandlerExtensions.ParentsSet._cache.
    private volatile ImmutableArray<ISubjectSource>[] _sources = [ImmutableArray<ISubjectSource>.Empty];
    private volatile ImmutableArray<SourceSubscription>[] _subscriptions = [ImmutableArray<SourceSubscription>.Empty];

    /// <summary>
    /// Internal: a monitor is only useful once it is registered as a lifecycle handler, which
    /// WithSourceMonitoring does. One constructed directly would never re-evaluate waits on a
    /// reparent, so this is not offered to consumers.
    /// </summary>
    internal SourceMonitor(Func<ILogger?>? loggerResolver = null)
    {
        _loggerResolver = loggerResolver;
        _initialHold = new RegistrationHold(this);
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public ImmutableArray<ISubjectSource> Sources => _sources[0];

    /// <summary>True when at least one public subscriber exists. Gates ownership event publishing.</summary>
    internal bool HasSubscribers => !_subscriptions[0].IsEmpty;

    /// <summary>
    /// Resolves the logger on first use (Subscribe, or a wait engine warning), since the context is
    /// configured before any logging provider exists (see WithSourceMonitoring).
    /// </summary>
    /// <remarks>
    /// Retries while the resolve returns null, and must keep doing so: with
    /// WithSourceMonitoring(services) the ILoggerFactory is only bridged into the context when the
    /// host is built, which is AFTER a consumer following the documented pattern has called
    /// Subscribe. Latching that null would silently kill every warning for the lifetime of the
    /// process, which is the exact defect this lazy resolution exists to avoid. The resolver is
    /// dropped once it succeeds, so its captured context is not retained past that point.
    /// <para>
    /// Read from arbitrary threads (a subscription's drain calls it when a handler throws), so it
    /// cannot rely on the monitor lock. Two threads racing here both resolve and store the same
    /// logger, which is harmless.
    /// </para>
    /// </remarks>
    internal ILogger? ResolveLogger()
    {
        var logger = Volatile.Read(ref _logger);
        if (logger is not null)
        {
            return logger;
        }

        var resolver = Volatile.Read(ref _loggerResolver);
        if (resolver is null)
        {
            return null;
        }

        logger = resolver.Invoke();
        if (logger is not null)
        {
            Volatile.Write(ref _logger, logger);
            Volatile.Write(ref _loggerResolver, null);
        }

        return logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The wait engine is the only thing here that cares about lifecycle changes: a reparent moves a
    /// branch's scope with no attach or detach event of its own, so satisfaction has to be
    /// re-evaluated on every parent-link mutation. That is gated on reference mutation rather than
    /// on subscriber count, because a pending wait is the one consumer with no subscription.
    /// <para>
    /// Nothing may escape from here. This runs inside LifecycleInterceptor's attach lock, which
    /// documents its handlers as exception-free, and a throw leaves the graph half-attached: later
    /// handlers are skipped, SubjectAttached never fires, and child properties never attach.
    /// </para>
    /// <para>
    /// Nothing else here needs lifecycle events: ownership events are published from
    /// SetSource/RemoveSource, and graph membership is a registry question, not a source one.
    /// </para>
    /// </remarks>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsPropertyReferenceAdded || change.IsPropertyReferenceRemoved)
        {
            try
            {
                OnWaitConditionChanged();
            }
            catch (Exception exception)
            {
                try
                {
                    ResolveLogger()?.LogError(
                        exception, "A synchronization wait re-evaluation threw and was ignored.");
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>Subscribes to the stream and captures the source snapshot atomically with the subscription.</summary>
    public SourceSubscription Subscribe(Action<SourceEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var subscription = new SourceSubscription(handler, _sources[0], Remove, this);
            _subscriptions = [_subscriptions[0].Add(subscription)];
            return subscription;
        }
    }

    private void Remove(SourceSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions = [_subscriptions[0].Remove(subscription)];
        }
    }

    /// <summary>Registers a source. Idempotent.</summary>
    /// <remarks>
    /// The re-evaluation below is defensive, not load-bearing: registering only ever adds an
    /// in-scope source, which can block a pending wait but never satisfy one, so no pending wait can
    /// complete because of it. Unregister's counterpart IS load-bearing - removing the source a wait
    /// is blocked on can satisfy it.
    /// </remarks>
    public void Register(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (_sources[0].Contains(source))
            {
                return;
            }

            _sources = [_sources[0].Add(source)];
            source.StateChanged += OnSourceStateChanged;

            Publish(new SourceEvent(
                SourceEventKind.SourceRegistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
        }

        OnWaitConditionChanged();
    }

    /// <summary>Unregisters a source. A no-op for a source that was never registered.</summary>
    public void Unregister(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (!_sources[0].Contains(source))
            {
                return;
            }

            _sources = [_sources[0].Remove(source)];
            source.StateChanged -= OnSourceStateChanged;

            Publish(new SourceEvent(
                SourceEventKind.SourceUnregistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
        }

        OnWaitConditionChanged();
    }

    private void OnSourceStateChanged(object? sender, SourceEvent sourceEvent)
    {
        lock (_lock)
        {
            // Dropped rather than published: a transition already in flight when Unregister ran
            // captured the handler list before the -=, so without this it would arrive after
            // SourceUnregistered, giving a consumer a state for a source it has already removed.
            // Both sides publish under this lock, so the check is decisive.
            if (!_sources[0].Contains(sourceEvent.Source))
            {
                return;
            }

            // Under _lock so this cannot overtake the SourceRegistered event Register publishes
            // under the same lock, having already attached this forwarder. A consumer tracking a
            // source from SourceRegistered would otherwise drop its first transition permanently,
            // since most sources transition once.
            Publish(sourceEvent);
        }

        OnWaitConditionChanged();
    }

    /// <summary>Enqueues an event onto every subscriber's own queue.</summary>
    private void Publish(in SourceEvent sourceEvent)
    {
        var subscriptions = _subscriptions[0];
        foreach (var subscription in subscriptions)
        {
            subscription.Enqueue(sourceEvent);
        }
    }

    /// <summary>
    /// Publishes under _lock, which is what keeps delivery ordered against a concurrent
    /// Register/Unregister/Subscribe. Cannot deadlock: Publish only enqueues onto a ConcurrentQueue
    /// and at most schedules a Task.Run, never running a handler synchronously or calling back into
    /// anything that takes _lock.
    /// </summary>
    internal void PublishUnderLock(in SourceEvent sourceEvent)
    {
        lock (_lock)
        {
            Publish(sourceEvent);
        }
    }

    // Born at 1, taken at WithSourceMonitoring time (before the host is built), so no wait can
    // complete until something explicitly releases it, regardless of hosted-service start order.
    // _initialHold is what CompleteSourceRegistration disposes: its own Interlocked latch (see
    // RegistrationHold.Dispose) is the idempotence guard, so a re-entrant loader guard calling
    // CompleteSourceRegistration more than once is safe without a second latch field here.
    private int _registrationHolds = 1;
    private readonly RegistrationHold _initialHold;

    /// <summary>True when no registration hold is outstanding, so waits may complete.</summary>
    public bool IsRegistrationComplete => Volatile.Read(ref _registrationHolds) == 0;

    /// <summary>
    /// Releases the initial hold, declaring that every source this application intends to start has
    /// been started and registered. Idempotent, so a re-entrant loader guard is safe.
    /// </summary>
    public void CompleteSourceRegistration() => _initialHold.Dispose();

    /// <summary>
    /// Takes a further hold for the duration of a later batch of source creation. Counted, so
    /// concurrent holders compose. Taking a hold blocks pending waits but never un-completes an
    /// already-completed one.
    /// </summary>
    public IDisposable DeferWaitCompletion()
    {
        Interlocked.Increment(ref _registrationHolds);

        // The hold is constructed before the re-evaluation, not after: a throw from any pending
        // wait's IsSatisfied would otherwise leave the count raised with nothing able to release it,
        // blocking every wait on this tree forever. Re-evaluating cannot complete a wait here
        // anyway - the increment guarantees IsSatisfied returns false - so the signal is defensive
        // and must not be able to strand the hold it belongs to.
        var hold = new RegistrationHold(this);
        OnWaitConditionChanged();
        return hold;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Explicit, so the domain-named <see cref="DeferWaitCompletion"/> stays the single method on
    /// this type's own surface. This is how Namotion.Interceptor.Hosting holds registration open
    /// between attaching a source to the graph and that source's queued StartAsync actually running,
    /// without either package referencing the other.
    /// </remarks>
    IDisposable IStartupCompletionDeferrer.DeferCompletion() => DeferWaitCompletion();

    private void ReleaseHold()
    {
        if (Interlocked.Decrement(ref _registrationHolds) == 0)
        {
            OnWaitConditionChanged();
        }
    }

    // Unlike _sources/_subscriptions, every access to _waits happens under _lock (see
    // OnWaitConditionChanged), so this needs neither the volatile field nor the box trick those two
    // use for lock-free reads.
    private ImmutableArray<PendingWait> _waits = ImmutableArray<PendingWait>.Empty;

    // Reused across every scope walk inside IsSatisfied (see SourceScope.IsInScope). Every caller of
    // IsSatisfied already holds _lock, and SearchGraph clears both collections before returning
    // (even on an early match), so reuse across sources within one pass, and across passes, is safe
    // and allocation-free. Otherwise a wait re-evaluation - which fires on every property-reference
    // add/remove tree-wide while any wait is pending - would allocate a HashSet and a Stack per
    // in-scope source check.
    private readonly HashSet<IInterceptorSubject> _scopeVisitedScratch = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<IInterceptorSubject> _scopePendingScratch = new();


    /// <summary>
    /// Completes when the branch containing <paramref name="subject"/> is synchronized: registration
    /// is complete, and every registered non-Stopped in-scope source is Synchronized. An empty
    /// in-scope set is vacuously satisfied once registration is complete (see IsSatisfied); before
    /// that, it blocks, since an empty scope is still ambiguous between "no source yet" and "no
    /// source ever" at that point.
    /// </summary>
    public Task WaitForSynchronizationAsync(
        IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        PendingWait wait;
        lock (_lock)
        {
            // Checked first with no PendingWait allocated: the common case for an application
            // re-awaiting per operation is already satisfied. Only the unsatisfied path below
            // allocates a PendingWait and its TaskCompletionSource.
            if (IsSatisfied(subject))
            {
                return Task.CompletedTask;
            }

            wait = new PendingWait(subject);
            _waits = _waits.Add(wait);
        }

        return wait.AwaitAsync(cancellationToken, () =>
        {
            lock (_lock)
            {
                _waits = _waits.Remove(wait);
            }
        });
    }

    private bool IsSatisfied(IInterceptorSubject anchor)
    {
        if (!IsRegistrationComplete)
        {
            return false;
        }

        foreach (var source in _sources[0])
        {
            if (!SourceScope.IsInScope(source, anchor, _scopeVisitedScratch, _scopePendingScratch))
            {
                continue;
            }

            var state = source.State;
            if (state != SourceState.Stopped && state != SourceState.Synchronized)
            {
                return false;
            }
        }

        // An empty scope, and a scope whose sources have all Stopped, both complete rather than
        // block. Once the application has declared registration complete it has asserted its sources
        // are set up, so a branch with nothing to wait for is a branch that is already synchronized.
        // Before registration is complete this method returned false above, so an empty scope still
        // blocks during startup.
        return true;
    }

    /// <summary>
    /// Re-evaluates every pending wait. Called from the hot property-reference-add/remove path (see
    /// HandleLifecycleChange) and from every other signaler (Register, Unregister, hold
    /// release/CompleteSourceRegistration, a registered source's own StateChanged).
    /// </summary>
    /// <remarks>
    /// Holds _lock across the whole pass, rather than re-acquiring it once per wait. A lock-free
    /// emptiness check used to gate this method as a performance optimization, but it caused a lost
    /// wakeup: a signal could observe "no waits yet" in the window between a waiter's IsSatisfied
    /// check and its add to _waits, with no later signal to re-evaluate it, hanging the wait forever.
    /// Holding _lock for the full pass serializes it with WaitForSynchronizationAsync's own
    /// check-and-add at least as strictly as before (a signal now runs fully before or fully after
    /// the whole pass, not just before or after each individual wait), so do not narrow this back to
    /// a per-wait or lock-free check. Completing a wait (TrySetResult) inside the lock is safe
    /// because PendingWait's TaskCompletionSource uses RunContinuationsAsynchronously: continuations
    /// run on the thread pool, never synchronously on this thread, so they cannot re-enter _lock here.
    /// </remarks>
    private void OnWaitConditionChanged()
    {
        List<Exception>? exceptions = null;
        lock (_lock)
        {
            if (_waits.IsEmpty)
            {
                return;
            }

            // A throw from one wait's IsSatisfied must not skip re-evaluating the rest - that would
            // be a lost wakeup for every wait after it. Written out rather than delegated to
            // ExceptionAggregation.ForEach, unlike the two cold call sites: this runs on every
            // property-reference add/remove tree-wide while any wait is pending, and the helper's
            // IEnumerable<T> parameter would box the ImmutableArray, heap-allocate its enumerator,
            // and allocate a closure per pass, since the lambda captures this.
            foreach (var wait in _waits)
            {
                try
                {
                    if (IsSatisfied(wait.Anchor))
                    {
                        wait.Complete();
                    }
                }
                catch (Exception exception)
                {
                    (exceptions ??= []).Add(exception);
                }
            }
        }

        ExceptionAggregation.ThrowIfAny(exceptions);
    }

    private sealed class PendingWait(IInterceptorSubject anchor)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IInterceptorSubject Anchor { get; } = anchor;

        public void Complete() => _completion.TrySetResult();

        public async Task AwaitAsync(CancellationToken cancellationToken, Action onFinished)
        {
            try
            {
                await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onFinished();
            }
        }
    }

    private sealed class RegistrationHold(SourceMonitor monitor) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                monitor.ReleaseHold();
            }
        }
    }
}

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// The per-tree registry of sources, the source event stream, and the synchronization waits.
/// Added to the tree root context by WithSourceMonitoring.
/// </summary>
/// <remarks>
/// Must run after ContextInheritanceHandler and ParentTrackingHandler: both maintain state that the
/// attach/detach catch-up scan and the topology-aware CurrentState depend on (the parent-context
/// fallback and the parent set respectively), so this handler needs their update to have already
/// happened for the same lifecycle change.
/// </remarks>
[RunsAfter(typeof(ContextInheritanceHandler), typeof(ParentTrackingHandler))]
public class SourceMonitor : ILifecycleHandler
{
    private readonly Lock _lock = new();
    private readonly Func<ILogger?>? _loggerResolver;

    private ILogger? _logger;

    // Boxed in a single-element array so the reference itself can be read with Volatile.Read: an
    // ImmutableArray<T> is a struct, which the generic Volatile/Interlocked helpers cannot target
    // directly. Writes always happen under _lock and replace the box wholesale (copy-on-write), so
    // Sources, HasSubscribers and Publish can read the latest published snapshot without taking the
    // lock on this hot path. Same technique as ParentsHandlerExtensions.ParentsSet._cache.
    private volatile ImmutableArray<ISubjectSource>[] _sources = [ImmutableArray<ISubjectSource>.Empty];
    private volatile ImmutableArray<SourceSubscription>[] _subscriptions = [ImmutableArray<SourceSubscription>.Empty];

    /// <summary>Creates a monitor. Prefer WithSourceMonitoring over calling this directly.</summary>
    public SourceMonitor(Func<ILogger?>? loggerResolver = null)
    {
        _loggerResolver = loggerResolver;
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public ImmutableArray<ISubjectSource> Sources => _sources[0];

    /// <summary>True when at least one public subscriber exists. Gates the attach and detach catch-up scan.</summary>
    internal bool HasSubscribers => !_subscriptions[0].IsEmpty;

    /// <summary>
    /// Resolves the logger on first use, from whichever call site needs it first: Subscribe, or one
    /// of the wait engine's diagnostic warnings. The context is configured before any logging
    /// provider exists (see WithSourceMonitoring), so a resolver deferred to first use is the only
    /// way an application that never calls Subscribe still gets its wait warnings logged. Every call
    /// site is already under _lock, so the read-then-maybe-write here needs no synchronization of
    /// its own.
    /// </summary>
    private ILogger? Logger => _logger ??= _loggerResolver?.Invoke();

    /// <inheritdoc />
    /// <remarks>
    /// The attach and detach hot paths pay one flag check (HasSubscribers) when nobody is
    /// listening. Pending waits deliberately do not count as subscribers: a wait is active during
    /// startup, exactly when attach storms happen, and never needs property events. A
    /// property-reference add or remove always calls OnWaitConditionChanged, which takes _lock to
    /// check for pending waits: an earlier lock-free emptiness check was removed because it raced
    /// WaitForSynchronizationAsync's own check-and-add and could drop the wakeup (see
    /// OnWaitConditionChanged). So an attach storm with no pending waits and no subscribers costs
    /// one flag read plus one uncontended lock acquisition per property.
    /// </remarks>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Branch scope reads mutable parent data, so a reparent changes the answer with no
        // registration or state transition. A same-tree reparent fires neither IsContextAttach nor
        // IsContextDetach, so gate on reference mutation, and never on the subscriber count: a wait
        // is exactly the consumer that has no subscription.
        if (change.IsPropertyReferenceAdded || change.IsPropertyReferenceRemoved)
        {
            OnWaitConditionChanged();
        }

        if (!HasSubscribers)
        {
            return;
        }

        if (change.IsContextAttach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyEnteredView);
        }
        else if (change.IsContextDetach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyLeftView);
        }
    }

    private void ScanSubject(IInterceptorSubject subject, SourceEventKind kind)
    {
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var name in subject.Properties.Keys)
        {
            var property = new PropertyReference(subject, name);
            if (!property.TryGetSource(out var source))
            {
                continue;
            }

            var entered = kind == SourceEventKind.PropertyEnteredView;
            Publish(new SourceEvent(
                kind, source, property,
                entered ? SourceState.Unclaimed : source.State,
                entered ? source.State : SourceState.Unclaimed,
                timestamp) { Monitor = this });
        }
    }

    /// <summary>Subscribes to the stream and captures the source snapshot atomically with the subscription.</summary>
    public SourceSubscription Subscribe(Action<SourceEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var subscription = new SourceSubscription(handler, _sources[0], Remove, Logger);
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
        Publish(sourceEvent);
        OnWaitConditionChanged();
    }

    /// <summary>Enqueues an event onto every subscriber's own queue.</summary>
    internal void Publish(in SourceEvent sourceEvent)
    {
        var subscriptions = _subscriptions[0];
        foreach (var subscription in subscriptions)
        {
            subscription.Enqueue(sourceEvent);
        }
    }

    /// <summary>
    /// Publishes while holding _lock, matching Register, Unregister and OnSourceStateChanged's own
    /// state changes. Used by property ownership changes (SetSource/RemoveSource), which mutate
    /// property data entirely outside this monitor, so this cannot make a newly-subscribed consumer
    /// see a claim that already happened before it subscribed: SourceSubscription.Sources has no
    /// ownership baseline to reconcile against (see docs/connectors-monitoring.md, Worked
    /// Sample). What it does close is delivery ordering: without the lock, an ownership event could
    /// be enqueued to a subscriber out of order relative to a concurrent Register/Unregister/
    /// Subscribe on the same monitor, since Publish itself is just an unsynchronized read of the
    /// current subscription list. Safe from deadlock: Publish only enqueues onto a ConcurrentQueue
    /// and, at most, schedules a Task.Run drain; it never invokes a subscriber handler synchronously
    /// and never calls back into any SourceMonitor method that takes _lock.
    /// </summary>
    internal void PublishUnderLock(in SourceEvent sourceEvent)
    {
        lock (_lock)
        {
            Publish(sourceEvent);
        }
    }

    // Born at 1. The monitor takes this hold at WithSourceMonitoring time, during context
    // configuration, before the host is even built, which is what makes signalling
    // order-independent without any argument about hosted service construction order.
    private int _registrationHolds = 1;
    private int _initialHoldReleased;

    /// <summary>True when no registration hold is outstanding, so waits may complete.</summary>
    public bool IsRegistrationComplete => Volatile.Read(ref _registrationHolds) == 0;

    /// <summary>
    /// Releases the initial hold, declaring that every source this application intends to start has
    /// been started and registered. Idempotent, so a re-entrant loader guard is safe.
    /// </summary>
    public void CompleteSourceRegistration()
    {
        if (Interlocked.Exchange(ref _initialHoldReleased, 1) == 1)
        {
            return;
        }

        ReleaseHold();
    }

    /// <summary>
    /// Takes a further hold for the duration of a later batch of source creation. Counted, so
    /// concurrent holders compose. Taking a hold blocks pending waits but never un-completes an
    /// already-completed one.
    /// </summary>
    public IDisposable DeferWaitCompletion()
    {
        Interlocked.Increment(ref _registrationHolds);
        OnWaitConditionChanged();
        return new RegistrationHold(this);
    }

    private void ReleaseHold()
    {
        if (Interlocked.Decrement(ref _registrationHolds) == 0)
        {
            OnWaitConditionChanged();
        }
    }

    // Unlike _sources/_subscriptions, every read and write of _waits happens under _lock (see
    // OnWaitConditionChanged for why the emptiness check cannot be lock-free), so this needs
    // neither the volatile field nor the box-per-publish trick those two use for lock-free reads.
    private ImmutableArray<PendingWait> _waits = ImmutableArray<PendingWait>.Empty;

    /// <summary>
    /// Completes when the branch containing <paramref name="subject"/> is synchronized: registration
    /// is complete, at least one in-scope source is registered, and every registered non-Stopped
    /// in-scope source is Synchronized.
    /// </summary>
    public Task WaitForSynchronizationAsync(
        IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        PendingWait wait;
        lock (_lock)
        {
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

    private bool IsSatisfied(IInterceptorSubject anchor, PendingWait? wait = null)
    {
        if (!IsRegistrationComplete)
        {
            return false;
        }

        var matched = false;
        var allInScopeStopped = true;
        foreach (var source in _sources[0])
        {
            if (!SourceScope.IsInScope(source, anchor))
            {
                continue;
            }

            matched = true;
            var state = source.State;
            if (state != SourceState.Stopped && state != SourceState.Synchronized)
            {
                return false;
            }

            if (state != SourceState.Stopped)
            {
                allInScopeStopped = false;
            }
        }

        if (!matched)
        {
            // Registration is complete and still nothing claims this branch. That is very likely a
            // misconfiguration, and blocking forever in silence reads as a hang rather than a
            // diagnosis, so say it once per wait rather than on every re-evaluation. MarkWarned is
            // only called here, at the point the warning is actually about to fire, so an unrelated
            // re-evaluation that finds the branch still matched never burns the one-shot flag.
            if (wait is not null && !wait.MarkWarned())
            {
                Logger?.LogWarning(
                    "A synchronization wait on {Subject} has no in-scope source, and source registration is complete. " +
                    "The wait will block until cancelled. Check that a source is configured for this branch.",
                    anchor.GetType().Name);
            }

            return false;
        }

        if (allInScopeStopped)
        {
            // Stopped is terminal, so this branch will never become live. Completing is more useful
            // than hanging, but silence would read as success, so say it out loud.
            Logger?.LogWarning(
                "A synchronization wait completed with every in-scope source stopped. " +
                "Stopped is terminal, so this branch will not synchronize again.");
        }

        return true;
    }

    /// <summary>
    /// Re-evaluates every pending wait. Called on the hot property-reference-add/remove path (see
    /// HandleLifecycleChange) as well as from every other signaler (Register, Unregister,
    /// CompleteSourceRegistration/ReleaseHold, and a registered source's own StateChanged).
    /// </summary>
    /// <remarks>
    /// The emptiness check below takes _lock rather than reading _waits lock-free. A lock-free
    /// check used to gate this method entirely, as a performance optimization, but it opened a lost
    /// wakeup: WaitForSynchronizationAsync's own check-and-add also runs under _lock, so without
    /// this method taking the same lock, a signal could read "no waits yet" and return in the
    /// window between the waiter's IsSatisfied check and its registration into _waits, and no later
    /// signal would ever re-evaluate it. Taking _lock here closes that window: the two critical
    /// sections now serialize, so a signal either runs before the waiter's check-and-add (and the
    /// waiter's own IsSatisfied then observes the new state directly) or after the wait is already
    /// published (and this method's re-evaluation picks it up). Correctness beats the
    /// micro-optimization the lock-free check existed for.
    /// </remarks>
    private void OnWaitConditionChanged()
    {
        ImmutableArray<PendingWait> waits;
        lock (_lock)
        {
            waits = _waits;
            if (waits.IsEmpty)
            {
                return;
            }
        }

        // A throw from one wait's IsSatisfied (a misbehaving anchor, a throwing logger) must not
        // stop the remaining waits in this pass from being re-evaluated - that would be a lost
        // wakeup for every wait after the throwing one, not just for the throwing one itself. Each
        // wait is isolated, and every exception collected is still surfaced to the caller once the
        // full pass has run, matching the same collect-then-rethrow pattern used by
        // SourceMonitoringExtensions.CompleteSourceRegistration and CompositeDisposable.Dispose.
        List<Exception>? exceptions = null;
        foreach (var wait in waits)
        {
            try
            {
                bool satisfied;
                lock (_lock)
                {
                    satisfied = IsSatisfied(wait.Anchor, wait);
                }

                if (satisfied)
                {
                    wait.Complete();
                }
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(exceptions);
        }
    }

    private sealed class PendingWait(IInterceptorSubject anchor)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _warned;

        public IInterceptorSubject Anchor { get; } = anchor;

        /// <summary>True once this wait has already logged its empty-scope warning.</summary>
        public bool MarkWarned() => Interlocked.Exchange(ref _warned, 1) == 1;

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

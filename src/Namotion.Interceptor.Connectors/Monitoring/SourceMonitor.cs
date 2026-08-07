using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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
    private readonly Func<ILogger?>? _loggerResolver;

    private ILogger? _logger;

    // Boxed in a single-element array so the reference can be read with Volatile.Read: ImmutableArray<T>
    // is a struct, which Volatile/Interlocked cannot target directly. Writes happen under _lock and
    // swap the box wholesale, so Sources, HasSubscribers and Publish can read the latest snapshot
    // lock-free. Same technique as ParentsHandlerExtensions.ParentsSet._cache.
    private volatile ImmutableArray<ISubjectSource>[] _sources = [ImmutableArray<ISubjectSource>.Empty];
    private volatile ImmutableArray<SourceSubscription>[] _subscriptions = [ImmutableArray<SourceSubscription>.Empty];

    /// <summary>Creates a monitor. Prefer WithSourceMonitoring over calling this directly.</summary>
    public SourceMonitor(Func<ILogger?>? loggerResolver = null)
    {
        _loggerResolver = loggerResolver;
        _initialHold = new RegistrationHold(this);
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public ImmutableArray<ISubjectSource> Sources => _sources[0];

    /// <summary>True when at least one public subscriber exists. Gates the attach and detach catch-up scan.</summary>
    internal bool HasSubscribers => !_subscriptions[0].IsEmpty;

    /// <summary>
    /// Resolves the logger on first use (Subscribe, or a wait engine warning), since the context is
    /// configured before any logging provider exists (see WithSourceMonitoring). Every call site is
    /// already under _lock, so the read-then-maybe-write here needs no synchronization of its own.
    /// </summary>
    private ILogger? Logger => _logger ??= _loggerResolver?.Invoke();

    /// <inheritdoc />
    /// <remarks>
    /// The wait engine is the only thing here that cares about lifecycle changes: a reparent moves a
    /// branch's scope with no attach or detach event of its own, so satisfaction has to be
    /// re-evaluated on every parent-link mutation. That is gated on reference mutation rather than
    /// on subscriber count, because a pending wait is the one consumer with no subscription.
    /// <para>
    /// Ownership events are published from SetSource/RemoveSource, where ownership actually changes,
    /// not from here. This monitor deliberately reports nothing about tree membership: a subject
    /// entering or leaving the graph is a registry question (see
    /// <c>ISubjectRegistry.TryGetRegisteredSubject</c>), not a source-ownership one.
    /// </para>
    /// </remarks>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsPropertyReferenceAdded || change.IsPropertyReferenceRemoved)
        {
            OnWaitConditionChanged();
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
        // Under _lock, not a bare Publish: Register attaches this forwarder BEFORE it publishes
        // SourceRegistered, both under _lock. A transition raised on another thread in that window
        // would otherwise enqueue ahead of the registration event, so a consumer that starts
        // tracking a source on SourceRegistered and applies NewState on StateChanged drops that
        // first transition and stays permanently one state stale (Connecting -> Synchronized fires
        // once for most sources, so there is no later event to correct it). Taking the lock here
        // makes the transition wait for Register to finish publishing. No new lock edge: this runs
        // inside the source's own transition lock, and OnWaitConditionChanged below already takes
        // _lock on this same path.
        PublishUnderLock(sourceEvent);
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
    /// Publishes under _lock. Register and Unregister do the same, because they publish alongside a
    /// mutation of _sources and the lock keeps that atomic with a concurrent Subscribe's snapshot;
    /// OnSourceStateChanged takes it to stay ordered behind the SourceRegistered event Register
    /// publishes under the same lock (see OnSourceStateChanged). Used by SetSource/RemoveSource,
    /// which mutate property data entirely outside this
    /// monitor, so an ownership event has no snapshot baseline to reconcile against the way
    /// registration does (see docs/connectors-monitoring.md, Worked Sample); only delivery order
    /// relative to a concurrent Register/Unregister/Subscribe needs protecting. Cannot deadlock:
    /// Publish only enqueues onto a ConcurrentQueue and, at most, schedules a Task.Run; it never runs
    /// a handler synchronously or calls back into anything that takes _lock.
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
        OnWaitConditionChanged();
        return new RegistrationHold(this);
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

    // Warn-once bookkeeping, keyed on the ANCHOR rather than on an individual wait. The intended
    // usage is an application re-awaiting per operation (see WaitForSynchronizationAsync), and an
    // already-satisfied wait returns on the fast path without ever allocating a PendingWait, so
    // per-wait flags left that path with nothing to deduplicate against: a per-request wait on a
    // legitimately local-only branch logged one Warning per request, forever. The diagnostic is a
    // property of the branch, not of one wait, so the flag belongs on the anchor.
    //
    // Stored in the subject's own data rather than in a table on this monitor, so the flag has
    // exactly the subject's lifetime and needs no weak references or cleanup: a side table would
    // either retain anchors forever or need weak keys to avoid it. Same (null, key) subject-scoped
    // convention ParentsHandlerExtensions and LifecycleInterceptorExtensions use. ConcurrentDictionary
    // .TryAdd IS the one-shot latch - it returns true exactly once per subject - so this needs no
    // lock of its own either, though every caller happens to hold _lock anyway.
    //
    // Two monitors sharing an anchor therefore share the flag, and only the first to reach it warns.
    // Multiple monitors over one subject is already an unusual topology, and this is a hint rather
    // than a verdict, so that is preferred over reintroducing per-monitor state.
    private const string EmptyScopeWarnedKey = "Namotion.Interceptor.Connectors.EmptyScopeWarned";
    private const string AllStoppedWarnedKey = "Namotion.Interceptor.Connectors.AllStoppedWarned";

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
            // re-awaiting per operation is already satisfied. Warning deduplication lives on the
            // anchor (see _anchorWarnings), not on the wait, precisely so this path deduplicates
            // too. Only the unsatisfied path below allocates a PendingWait (and its
            // TaskCompletionSource).
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

        var matched = false;
        var allInScopeStopped = true;
        foreach (var source in _sources[0])
        {
            if (!SourceScope.IsInScope(source, anchor, _scopeVisitedScratch, _scopePendingScratch))
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
            // Warn once per anchor, not on every re-evaluation and not on every call. TryAdd both
            // tests and sets in one step, and only here, right before the warning fires, so a later
            // pass that finds the branch matched again never burns the flag.
            if (anchor.Data.TryAdd((null, EmptyScopeWarnedKey), null))
            {
                Logger?.LogWarning(
                    "A synchronization wait on {Subject} has no in-scope source, and source registration is complete. " +
                    "The wait completes immediately: once registration is complete an empty scope is no longer " +
                    "ambiguous between \"no source yet\" and \"no source ever\", so it definitively means this " +
                    "branch is local-only and vacuously synchronized. Check that a source is configured for this " +
                    "branch if that is unexpected.",
                    anchor.GetType().Name);
            }

            // The blocking rule predates the registration-complete signal. Once the application has
            // called CompleteSourceRegistration, an empty scope is no longer ambiguous, so it is
            // vacuously satisfied - consistent with the all-Stopped rule below, which also completes
            // vacuously rather than hanging. Before registration is complete this method already
            // returned false above, so an empty scope still blocks during startup.
            return true;
        }

        // Same one-shot-per-anchor treatment as the empty-scope warning above, and for the same
        // reasons: a wait can be re-evaluated again (e.g. by an unrelated Register/Unregister
        // elsewhere in the tree) after it has already completed, in the window before its
        // continuation removes it from _waits, and an application re-awaiting per operation would
        // otherwise log once per call forever.
        if (allInScopeStopped && anchor.Data.TryAdd((null, AllStoppedWarnedKey), null))
        {
            // Stopped is terminal, so this branch will never become live. Completing beats
            // hanging, but silence would look like success, so log it.
            Logger?.LogWarning(
                "A synchronization wait on {Subject} completed with every in-scope source stopped. " +
                "Stopped is terminal, so this branch will not synchronize again.",
                anchor.GetType().Name);
        }

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
            // be a lost wakeup for every wait after it. Collect and rethrow once the full pass
            // completes, matching SourceMonitoringExtensions.CompleteSourceRegistration and
            // CompositeDisposable.Dispose (see ExceptionAggregation, shared with both).
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

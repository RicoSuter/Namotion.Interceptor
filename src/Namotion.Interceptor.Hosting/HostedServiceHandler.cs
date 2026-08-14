using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

[RunsAfter(typeof(ContextInheritanceHandler))]
internal sealed class HostedServiceHandler : IHostedService, ILifecycleHandler
{
    // A workaround, not a design choice: the generated context constructor attaches the subject
    // before the caller has assigned anything, so "new Car(context) { Name = "x" }" would otherwise
    // start a service that reads a half built subject. Paid by the stop body as well as the start,
    // which is why it is not named for the start.
    // See docs/design/hosting-service-ownership.md#the-50-ms-delay.
    private const int TransitionDelayMilliseconds = 50;

    private readonly Func<ILogger?> _loggerResolver;
    private readonly HostedServiceGate _gate = new();
    private readonly ConcurrentDictionary<HostedServiceTarget, IInterceptorSubject> _running = new();
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _liveSubjects = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlightStops = new();

    private ILogger? _logger;

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    private ILogger? Logger => _logger ??= _loggerResolver();

    /// <summary>
    /// Test seam, awaited in <see cref="StopAsync"/> after the drain begins and before liveness is
    /// cleared. Null in production. A start appended while it is held sees a draining gate and a still
    /// live subject, which is the interleaving the start body's gate re-read exists for.
    /// </summary>
    internal Func<Task>? DrainGate { get; set; }

    /// <summary>
    /// Test seam, invoked after the take and the running set entry and before the gate re-read. Null in
    /// production. Holds open the window in which a handler that passed the gate read on entry owns a
    /// target it may never start.
    /// </summary>
    internal Action? OwnershipTakenGate { get; set; }

    /// <summary>
    /// Test seam, invoked inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback,
    /// between the membership answer and the liveness write. Null in production. Holding it holds the
    /// graph mutation lock, which is the property it makes observable.
    /// </summary>
    internal Action? LivenessWriteGate { get; set; }

    /// <summary>
    /// Test seam, invoked inside the chain lock between the liveness read and the ownership take. Null
    /// in production. Holds open the window in which a context detach reads the owner as null and
    /// releases nothing, so the take that follows is one no release reaches.
    /// </summary>
    internal Action? LivenessReadGate { get; set; }

    /// <summary>
    /// Test seam, invoked inside the chain lock between a stop's in flight join and its running set
    /// removal. Null in production. Holding it holds the only moment a stop is in both sets, which is
    /// what the drain's read order turns into "always in at least one".
    /// </summary>
    internal Action? StopBookkeepingGate { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Runs inside LifecycleInterceptor's lock, so everything here only appends, which never blocks
        // and never runs a body. Third party code does run under that lock through TakeStartupHolds,
        // an accepted hazard with a constraint on the implementer: see IStartupCompletionDeferrer and
        // residual hazard 4 in docs/design/hosting-service-ownership.md.
        if (change.IsContextAttach)
        {
            AttachSubject(change.Subject);
        }
        else if (change.IsContextDetach)
        {
            DetachSubject(change.Subject);
        }
    }

    private void AttachSubject(IInterceptorSubject subject)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            return;
        }

        var subjectTarget = subject is IHostedService hostedService
            ? hostedService.GetOrAddSubjectTarget()
            : null;

        var attachments = subject.GetHostedServiceAttachments();
        if (subjectTarget is null && attachments.IsEmpty)
        {
            // The common case, and why liveness is recorded here rather than for every attaching
            // subject: every reader of liveness holds a target, so a subject with no target has no
            // reader. MarkLiveIfAttached covers the one moment that is not true.
            return;
        }

        _liveSubjects[subject] = 0;

        if (subjectTarget is not null)
        {
            TryTakeOwnershipAndStart(subject, subjectTarget);
        }

        foreach (var attachment in attachments)
        {
            TryTakeOwnershipAndStart(subject, ((IHostedServiceAttachmentTarget)attachment).Target);
        }

        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after the write, for the same reason the target guard re-reads after its own:
            // a liveness entry that lands after the drain cleared the set roots the subject on a dead
            // handler for the rest of that handler's life.
            _liveSubjects.TryRemove(subject, out _);
        }
    }

    private void DetachSubject(IInterceptorSubject subject)
    {
        // Read before anything is allocated: a completion source per subject is 1.76 MB of garbage per
        // detach of a 20,000 subject graph, under the lifecycle lock. The type test stands in for a
        // second data lookup, since only AttachSubject creates a subject target and only for an
        // IHostedService.
        //
        // "Has ever hosted", not "hosts now": they differ for a subject whose last attachment was
        // detached while it stayed in the graph, and skipping the clear there leaves an entry that
        // outlives its membership, which a queued start reads and acts on.
        var everHosted = subject.TryGetHostedServiceAttachments(out var attachments);
        var subjectTarget = subject is IHostedService ? subject.TryGetSubjectTarget() : null;
        if (subjectTarget is null && !everHosted)
        {
            return;
        }

        // Liveness is per subject and cleared here, under the lifecycle lock. It cannot be per target:
        // one subject reachable from two hosting enabled contexts shares a single target with two
        // handlers, and both are live for it while only one of them owns it.
        _liveSubjects.TryRemove(subject, out _);

        if (subjectTarget is null && attachments.IsEmpty)
        {
            // Hosted something once, hosts nothing now. Liveness is gone, and there is no target left
            // to stop or release.
            return;
        }

        // A handler stops what it owns and nothing else, or it disposes an instance another handler
        // created and is running. Not readable from the transition body either, since ownership is
        // released just below and the body would always see a stranger.
        //
        // Appended now, never deferred into another transition: deferring disposes the instance a
        // re-attach created and leaks the one this detach meant to stop. Walkthrough in
        // docs/design/hosting-service-ownership.md#why-a-composite-transition-is-wrong.
        TaskCompletionSource? subjectStopped = null;
        if (subjectTarget is not null && ReferenceEquals(subjectTarget.Owner, this))
        {
            // Allocated only when there is an attachment to order behind it: the signal exists to hold
            // the attachment stops until the subject's own stop returned, and nothing else reads it.
            subjectStopped = attachments.IsEmpty
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            AppendStop(subject, subjectTarget, subjectStopped, waitFor: null, CancellationToken.None);
        }

        foreach (var attachment in attachments)
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (ReferenceEquals(target.Owner, this))
            {
                // A null wait is the "nothing to order behind" case, which is what the subject target
                // being absent or owned by another handler means. The stop body skips it, so that
                // case allocates no completed task to await.
                AppendStop(subject, target, signal: null, waitFor: subjectStopped?.Task, CancellationToken.None);
            }
        }

        // Released after the stops are appended, and never from inside a transition body: releasing
        // from the body would clobber ownership a re-attach has already retaken, and the re-attach's
        // start would then no-op itself. Releasing a target this handler does not own is a no-op.
        subjectTarget?.ReleaseOwnership(this);
        foreach (var attachment in attachments)
        {
            ((IHostedServiceAttachmentTarget)attachment).Target.ReleaseOwnership(this);
        }
    }

    /// <summary>
    /// Takes ownership of the target for this handler and appends its start, returning the appended
    /// transition. Returns null when the handler took nothing, because the subject is no longer live
    /// for it, because another handler owns the target, or because this handler is draining.
    /// </summary>
    /// <remarks>
    /// The transition carries no cancellation token: a caller's token bounds its wait, never the
    /// transition, or cancelling an <c>AttachHostedServiceAsync</c> await would abort a start already
    /// under way and record it as a failure.
    /// </remarks>
    internal Task? TryTakeOwnershipAndStart(IInterceptorSubject subject, HostedServiceTarget target)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // A draining or drained handler must not take ownership. Nothing it owns can ever start,
            // and a target left owned by a dead handler makes every future handler over that subject
            // lose the compare and exchange. Read here as well as after the append so the ordinary
            // drained case never installs an owner that a live handler could race against at all.
            return null;
        }

        // Taken here, synchronously, and not inside the body: appending never runs the body, so the
        // service is not running when the attach returns, and anything that treats "the graph has
        // finished starting" as a completion point would otherwise reach it while this start is still
        // queued. Taking the hold before the append leaves no window in which that can happen.
        var startupHolds = TakeStartupHolds(subject.Context);

        // The running set entry is written inside the chain lock, together with the take and the append,
        // rather than here: see the comment at the call back. Writing it only on the path where the take
        // succeeded keeps the old guarantee that a target this handler failed to take never enters the
        // set, which would make the drain stop an instance another handler is running.
        var start = target.TryTakeOwnershipAndAppendAsync(
            this,
            subject,
            () => RunStartAsync(subject, target, startupHolds),
            () => _running[target] = subject,
            out var ownershipTaken);

        if (start is null)
        {
            ReleaseStartupHolds(startupHolds);
            return null;
        }

        OwnershipTakenGate?.Invoke();

        if (ownershipTaken && _gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after both writes, which turns the check at the top from a narrowing into a
            // guard: reading Running still here proves the drain had not begun when they landed, so its
            // snapshot covers this target. Any later read may already have been swept past.
            //
            // Outside the chain lock only because a draining handler installs nothing, so no take of
            // this handler's can be in flight for ReleaseOwnership to clobber, matching as it does on
            // the handler rather than on the take. The liveness equivalent has no such guarantee and
            // lives inside the chain lock.
            //
            // Only an ownership this call installed. An earlier attach's may be running, and undoing
            // that one would pull it out of the set the drain is about to stop.
            _running.TryRemove(target, out _);
            target.ReleaseOwnership(this);
        }

        return start;
    }

    private async Task RunStartAsync(IInterceptorSubject subject, HostedServiceTarget target, IDisposable[] startupHolds)
    {
        try
        {
            await _gate.WaitForOpenAsync().ConfigureAwait(false);
            if (_gate.State != HostedServiceGateState.Running)
            {
                // Inside the body, never at append time: a start queued when shutdown begins has to
                // re-read, and a body skipped at append time would never run its signalling.
                return;
            }

            // Two windows, so neither condition is redundant: a detach clears liveness before it
            // releases ownership, so a body reading in between is refused by liveness alone, while one
            // appended after the detach finished is refused by ownership alone. No test separates them,
            // because holding a body in that window means holding the lifecycle lock.
            if (!_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
            {
                return;
            }

            if (target.Current is not null)
            {
                // One instance per target, checked in the body where the chain serializes it. Ownership
                // stops a second handler, but a subject reachable from two hosting enabled contexts
                // raises one context attach per context, and the owning handler sees both: measured as
                // StartCount 2 without this guard.
                return;
            }

            // Cleared after every guard, never before: a start that is gated out or skipped must not
            // drop a fault that a caller has not read yet.
            target.SetFault(null);

            try
            {
                await Task.Delay(TransitionDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

                var instance = target.Subject ?? target.Factory!();
                if (target.IsHandlerOwnedInstance && !target.TryRecordFactoryInstance(instance))
                {
                    // Refused for every factory attachment, whatever the instance is, and recorded as
                    // a fault because that is the channel the caller already reads. Why it fails
                    // closed rather than only where the repeat would be a use after dispose is in
                    // docs/design/hosting-service-ownership.md#faults-and-failed-starts.
                    throw new InvalidOperationException(
                        "The hosted service factory returned the instance it returned last time. The handler owns " +
                        "every instance it creates and stops it when the subject leaves the graph, disposing it as " +
                        "well when it is disposable, so the factory must construct a new one on every call.");
                }

                try
                {
                    await instance.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    if (target.IsHandlerOwnedInstance)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }

                    throw;
                }

                target.SetCurrent(instance);
            }
            catch (Exception exception)
            {
                target.SetFault(exception);
                Logger?.LogError(exception, "Failed to start hosted service for subject {Subject}.", subject);
            }
        }
        finally
        {
            // In a finally, so every way out releases.
            ReleaseStartupHolds(startupHolds);
        }
    }

    /// <summary>
    /// Takes a completion hold on every deferrer reachable from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Empty for an application with no deferring subsystem, the common case. The constraint this call
    /// site puts on an implementer is on <see cref="IStartupCompletionDeferrer"/>.
    /// </remarks>
    private IDisposable[] TakeStartupHolds(IInterceptorSubjectContext context)
    {
        var deferrers = context.GetServices<IStartupCompletionDeferrer>();
        if (deferrers.IsEmpty)
        {
            return [];
        }

        var holds = new IDisposable[deferrers.Length];
        var taken = 0;
        foreach (var deferrer in deferrers)
        {
            try
            {
                holds[taken] = deferrer.DeferCompletion();
                taken++;
            }
            catch (Exception exception)
            {
                // One deferrer throwing must not abandon the holds already taken, and must not
                // propagate: an attach runs under the lifecycle lock inside a property write, so the
                // exception would surface at an unrelated assignment.
                Logger?.LogError(exception, "Taking a startup completion hold threw and was ignored.");
            }
        }

        return taken == holds.Length ? holds : holds[..taken];
    }

    private void ReleaseStartupHolds(IDisposable[] startupHolds)
    {
        foreach (var hold in startupHolds)
        {
            try
            {
                hold.Dispose();
            }
            catch (Exception exception)
            {
                // One deferrer throwing must not strand the others, for the same reason the release
                // sits in a finally at all.
                Logger?.LogError(exception, "Releasing a startup completion hold threw and was ignored.");
            }
        }
    }

    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
    {
        var stop = target.AppendAsync(async () =>
        {
            try
            {
                if (waitFor is not null)
                {
                    // Orders a subject's stop ahead of its attachments. Acyclic: the subject's chain
                    // waits on nothing. A hosted service must therefore not detach an attachment from
                    // inside its own stop path, or this becomes a cycle.
                    await waitFor.ConfigureAwait(false);
                }

                // Waited for, but not read: a stop runs at every state, Drained included, because one
                // appended after the drain snapshotted everything would otherwise never stop and never
                // dispose its instance. The null check below is what makes a stop idempotent.
                await _gate.WaitForOpenAsync().ConfigureAwait(false);

                var instance = target.Current;
                if (instance is null)
                {
                    return;
                }

                target.SetCurrent(null);

                await Task.Delay(TransitionDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await instance.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caught rather than filtered out, and not recorded as a fault: a cancelled stop
                    // is the caller's token expiring, not a failure, but the dispose below still has
                    // to run. Current is already cleared, so an instance that escaped here would be
                    // unreachable and never disposed, which is the ordinary ShutdownTimeout path.
                }
                catch (Exception exception)
                {
                    target.SetFault(exception);
                    Logger?.LogError(exception, "Failed to stop hosted service for subject {Subject}.", subject);
                }

                if (target.IsHandlerOwnedInstance)
                {
                    await DisposeInstanceAsync(instance).ConfigureAwait(false);
                }
            }
            finally
            {
                // Always signals, including on the gated-out and cancelled paths, or a paired
                // attachment stop parks forever on a signal that is never set.
                signal?.TrySetResult();
            }
        },
        onAppended: appended =>
        {
            // Joins the in flight set before leaving the running set, which is one half of the barrier;
            // StopAsync reading the running set before the in flight set is the other. A drain that
            // finds the target already gone from the running set is therefore reading the in flight set
            // after this join and sees the stop. Removing first leaves a gap in which the stop is in
            // neither set, and a drain reading both inside it waits for nothing and returns while this
            // stop is still about to touch a service provider the host is disposing.
            //
            // Inside the chain lock only so a drain that does find the target has to queue behind this
            // append rather than race it.
            TrackInFlightStop(appended);
            StopBookkeepingGate?.Invoke();
            _running.TryRemove(target, out _);
        });

        return stop;
    }

    /// <summary>
    /// Registers a stop so <see cref="StopAsync"/> can be a barrier for it. A target leaves the
    /// running set when its stop is appended, so no running set snapshot covers a stop queued before
    /// the drain.
    /// </summary>
    private void TrackInFlightStop(Task stop)
    {
        _inFlightStops[stop] = 0;

        _ = stop.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _inFlightStops,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeInstanceAsync(IHostedService instance)
    {
        try
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            // Here for the log, not for containment: this runs in a transition body, and the chain's own
            // catch would swallow an escape from here anyway, silently. Every other guard in this file
            // has an observable behaviour behind it; this one has only the report.
            Logger?.LogError(exception, "Failed to dispose hosted service {Service}.", instance.ToString());
        }
    }

    /// <summary>
    /// Opens the startup gate if it has never been opened. A one way ratchet: it does nothing once the
    /// handler is draining or drained.
    /// </summary>
    internal void EnsureStarted() => _gate.EnsureStarted();

    /// <summary>
    /// Waits for the start this handler appended for the subject and rethrows the fault it recorded,
    /// so a subject that fails to start aborts host startup the way <c>AddHostedService</c> does.
    /// Returns false when nothing was started, which the caller must not read as a start.
    /// </summary>
    internal async Task<bool> WaitForStartAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        // Reads the target and never creates one, and never takes ownership: this handler either
        // already claimed the target in AttachSubject or has no business claiming it. A drained
        // handler releases only what its own drain snapshotted, so a claim taken here would never be
        // released and the next handler over the same subject would lose the compare and exchange
        // forever. The liveness read is the same guard the attach paths use, and it is what excludes
        // a draining or drained handler, which has no start queued and never will have one.
        var target = subject.TryGetSubjectTarget();
        if (target is null || !_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
        {
            return false;
        }

        // An empty transition on the same chain. Appending never runs a body, so this completes only
        // once the start appended ahead of it has run.
        await target
            .AppendAsync(() => Task.CompletedTask)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target.Fault is { } fault)
        {
            // Captured rather than rethrown, for the reason on AttachHostedServiceAsync: this is the
            // exception a failing subject aborts host startup with, so it is the one users read.
            ExceptionDispatchInfo.Capture(fault).Throw();
        }

        // Read after the fault, and read at all because the guards above cannot cover a drain that
        // begins while this wait is queued behind the start: the start body then gates itself out and
        // sets nothing, and only the start body ever sets Current.
        return target.Current is not null;
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    /// <summary>
    /// Whether this handler holds the target in the set its drain would stop. Test only: a target left
    /// here for a subject that has left the graph is the leak, not an observable behaviour.
    /// </summary>
    internal bool IsRunning(HostedServiceTarget target) => _running.ContainsKey(target);

    /// <summary>
    /// Records liveness for a subject already in the graph that hosted nothing when it entered, so
    /// <c>AttachSubject</c> recorded none. The one moment the answer cannot be taken from a target.
    /// </summary>
    /// <remarks>
    /// The write happens inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback, not
    /// after it: reading membership and then writing releases the lock in between, and a graph move
    /// landing in that gap makes the write land on the opposite answer.
    /// <para>
    /// Only the write needs that, not the take after it, which reads liveness under the chain lock and
    /// refuses on its own. Keeping the take outside also keeps this off the list of places that run an
    /// <see cref="IStartupCompletionDeferrer"/> under the graph lock.
    /// </para>
    /// <para>
    /// Every reachable interceptor is asked, because one not holding the subject says nothing about
    /// another. A handler can therefore be marked live on the strength of a graph it does not serve;
    /// see the multi context note in docs/design/hosting-service-ownership.md.
    /// </para>
    /// </remarks>
    internal void MarkLiveIfAttached(IInterceptorSubject subject)
    {
        if (_gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            return;
        }

        var interceptors = subject.Context.GetServices<LifecycleInterceptor>();

        // Hoisted, so several interceptors cost one delegate rather than one each.
        var record = () =>
        {
            LivenessWriteGate?.Invoke();
            _liveSubjects[subject] = 0;
        };

        var recorded = false;
        for (var index = 0; index < interceptors.Length && !recorded; index++)
        {
            recorded = interceptors[index].TryRunWhileAttached(subject, record);
        }

        if (recorded && _gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after the write, for the same reason AttachSubject re-reads after its own: an
            // entry that lands after the drain cleared the set roots the subject on a dead handler for
            // the rest of that handler's life.
            _liveSubjects.TryRemove(subject, out _);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        if (DrainGate is { } drainGate)
        {
            await drainGate().ConfigureAwait(false);
        }

        // Liveness ends where the drain begins. A handler that still reports a subject as live claims
        // ownership of every attachment added to it afterwards, appends a start that no-ops, and never
        // releases it, because the release loop below only covers the drain's own snapshot.
        _liveSubjects.Clear();

        var snapshot = _running.ToArray();

        // Read after the running set, never before it, and that order is the barrier's other half.
        // AppendStop joins this set before it leaves the running one, so a target the snapshot above
        // missed had already been removed, which means its stop had already joined here and this read
        // finds it. Reading this one first inverts the argument and leaves a window in which a stop is
        // in neither, and the drain then waits for nothing and returns while that stop is still running.
        //
        // Nothing appended after this read is missed either: it is either for a target the snapshot
        // above still held, so the drain appended its own stop behind it on the same chain and awaits
        // that one, or it is for a target with no instance, which the stop body no-ops.
        var queuedStops = _inFlightStops.Keys;
        var stops = new List<Task>(snapshot.Length + queuedStops.Count);

        // Shutdown uses the same shape per owned subject as a context detach does, rather than
        // stopping every target independently: a subject's own stop has to return before the
        // attachments it uses are stopped and disposed underneath it.
        Dictionary<IInterceptorSubject, TaskCompletionSource>? subjectStops = null;
        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is null)
            {
                continue;
            }

            var subjectStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (subjectStops ??= new Dictionary<IInterceptorSubject, TaskCompletionSource>())[subject] = subjectStopped;
            stops.Add(AppendStop(subject, target, subjectStopped, waitFor: null, cancellationToken));
        }

        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is not null)
            {
                continue;
            }

            var waitFor = subjectStops is not null && subjectStops.TryGetValue(subject, out var subjectStopped)
                ? subjectStopped.Task
                : null;

            stops.Add(AppendStop(subject, target, signal: null, waitFor, cancellationToken));
        }

        stops.AddRange(queuedStops);

        try
        {
            // Bounded by the token, which for a host is the shutdown deadline. Nothing further down
            // observes it: the chain waits inside a stop body are untokened by design, and a stop that
            // is wedged behind one of them would otherwise hold the process open forever. A service
            // that ignores its own stop token, or a chain wedged by the forbidden self detach shape,
            // is exactly what this barrier has to give up on.
            await Task.WhenAll(stops).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Swallowed rather than propagated, and the drain still finishes below. Rethrowing would
            // abandon the ownership release, so every target this handler owns would stay owned by a
            // dead handler and a second host over the same subjects would start nothing. The host
            // treats an exception here as a failed shutdown and disposes the provider anyway, so
            // there is nothing to gain and the whole cleanup to lose.
            Logger?.LogWarning(
                "Shutdown gave up waiting for {Count} hosted service transitions; they keep running unobserved.",
                stops.Count);
        }

        foreach (var (target, _) in snapshot)
        {
            // Released after the stops so a second host cannot start ahead of this host's stop,
            // and released at all so a second host over the same subjects is not blocked forever.
            target.ReleaseOwnership(this);
        }

        // A drained handler is still reachable from the context that created it, so it must not keep
        // rooting the subjects and targets it has seen.
        _running.Clear();

        _gate.CompleteDraining();
    }
}

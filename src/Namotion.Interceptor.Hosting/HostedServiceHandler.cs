using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

// No longer IDisposable: the old implementation existed only to cancel the action loop's token
// source, and there is no such token under per target chains.
[RunsAfter(typeof(ContextInheritanceHandler))]
internal sealed class HostedServiceHandler : IHostedService, ILifecycleHandler
{
    private const int StartDelayMilliseconds = 50;

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
    /// Test seam, awaited in <see cref="StopAsync"/> after the gate begins draining and after the
    /// queued stops are snapshotted, but before liveness is cleared and before the running set is
    /// snapshotted. Null in production, where the statements it sits between are adjacent. The
    /// placement is what makes the drain window observable: a start appended while this is held sees
    /// a draining gate and a still live subject, which is the interleaving the start body's gate
    /// re-read exists for.
    /// </summary>
    internal Func<Task>? DrainGate { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Invoked from inside LifecycleInterceptor's lock (_attachedSubjects). Everything here only
        // appends, and appending never blocks and never runs a transition body.
        //
        // Third party code does run under that lock, though, and calling it a hazard is more honest
        // than calling it impossible: an attach takes a startup completion hold from every
        // IStartupCompletionDeferrer on the context, and a refused append disposes those holds again,
        // both synchronously (see TakeStartupHolds). A deferrer that takes a lock of its own therefore
        // joins the lock order of this lock, and a thread holding that lock while waiting for a
        // transition that needs this one wedges all three. The hold has to exist before the append
        // completes, and the event that appends arrives already inside the lock, so there is nowhere
        // else to take it without reopening the window the hold exists to close.
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

        _liveSubjects[subject] = 0;

        if (subject is IHostedService hostedService)
        {
            TryTakeOwnershipAndStart(subject, subject.GetOrAddSubjectTarget(hostedService));
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
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
        // Liveness is per subject and cleared here, under the lifecycle lock. It cannot be per target,
        // because the attaching path takes target ownership itself and would pass its own check.
        _liveSubjects.TryRemove(subject, out _);

        var subjectStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subjectTarget = subject.TryGetSubjectTarget();
        var attachments = subject.GetHostedServiceAttachments();

        // Ownership is read here and only here. A handler stops what it owns and nothing else:
        // otherwise this detach stops and disposes an instance another handler created and is
        // running, either a live handler that took over from this drained one or a sibling handler
        // that lost the compare and exchange. It cannot move into the transition body either,
        // because ownership is released a few lines below, so the body would always see a stranger.
        //
        // Stops are appended NOW, not issued later from inside another transition. Deferring them
        // lets a re-attach's create land first on the attachment chain, after which the deferred stop
        // disposes the NEW instance and leaks the old one.
        if (subjectTarget is not null && ReferenceEquals(subjectTarget.Owner, this))
        {
            AppendStop(subject, subjectTarget, subjectStopped, waitFor: null, CancellationToken.None);
        }
        else
        {
            subjectStopped.TrySetResult();
        }

        foreach (var attachment in attachments)
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (ReferenceEquals(target.Owner, this))
            {
                AppendStop(subject, target, signal: null, waitFor: subjectStopped.Task, CancellationToken.None);
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
    /// The appended transition deliberately carries no cancellation token: a caller's token bounds its
    /// wait for the transition, never the transition itself, or cancelling an
    /// <c>AttachHostedServiceAsync</c> await would abort a start that is already under way and record
    /// the cancellation as a start failure.
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
        // service is not running when the attach returns. Anything that treats "the graph has
        // finished starting" as a completion point would otherwise reach it while this start is
        // still queued - concretely, a source attached here would not have registered with its
        // SourceMonitor yet, and a synchronization wait would complete against a tree that is not
        // synchronized. Taking the hold before the append leaves no window in which that can happen.
        //
        // Nested attaches compose because holds are counted: a service that attaches children during
        // its own StartAsync takes their holds before its own is released below.
        var startupHolds = TakeStartupHolds(subject.Context);

        var start = target.TryTakeOwnershipAndAppendAsync(
            this,
            subject,
            _ => RunStartAsync(subject, target, startupHolds),
            CancellationToken.None,
            out var ownershipTaken);

        if (start is null)
        {
            ReleaseStartupHolds(startupHolds);
            return null;
        }

        // Joined after the take rather than before it: a running set entry for a target this handler
        // failed to take would make the drain stop and dispose an instance another handler created
        // and is running.
        _running[target] = subject;

        if (ownershipTaken && _gate.State is HostedServiceGateState.Draining or HostedServiceGateState.Drained)
        {
            // Re-read after both writes, which is what turns the check at the top from a narrowing
            // into a guard. Reading Running here proves the drain had not begun when the two writes
            // landed, so its own snapshot covers this target and its release loop reaches it. Reading
            // anything later means the drain may already have swept past, so the take is undone here
            // rather than left to a release loop that will never see it.
            //
            // Only an ownership this call installed is undone. Finding this handler already installed
            // means an earlier attach owns a target that may be running, and undoing that one would
            // pull it out of the running set the drain is about to stop.
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
                // Read inside the body, never at append time: a start already queued when shutdown
                // begins must re-read the state, and a body skipped at append time would never run
                // its signalling.
                return;
            }

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
                await Task.Delay(StartDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

                var instance = target.Subject ?? target.Factory!();
                if (target.IsHandlerOwnedInstance && !target.TryRecordFactoryInstance(instance))
                {
                    // The handler disposes every instance it creates, so a factory that hands back
                    // what it handed back last time hands back a disposed instance. Enforced rather
                    // than documented, because it is the one shape a caller migrating from the old
                    // instance based API is steered into: "AttachHostedService(myService)" no longer
                    // compiles and "AttachHostedService(() => myService)" does. Recorded as a fault,
                    // which is the channel that caller already reads.
                    throw new InvalidOperationException(
                        "The hosted service factory returned the instance it returned last time. The handler " +
                        "disposes every instance it creates, so the factory must construct a new one on every call.");
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
            // In a finally, so every way out releases: gated out by a drain, not live, skipped by
            // the one instance guard, or a start that threw. A leaked hold blocks every
            // synchronization wait on the tree forever, which is a hang rather than a wrong
            // answer, and worse than never having taken the hold.
            ReleaseStartupHolds(startupHolds);
        }
    }

    /// <summary>
    /// Takes a completion hold on every deferrer reachable from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Empty for an application that configures no deferring subsystem (no source monitoring, for
    /// example), which is the common case and costs one empty check per attach.
    /// <para>
    /// <c>DeferCompletion</c> is third party code and, on the lifecycle driven path, it runs under
    /// LifecycleInterceptor's lock. A deferrer must therefore not block on anything that can be waiting
    /// for a transition, and must not take a lock that a thread inside that lock can be waiting for.
    /// See the note on <see cref="HandleLifecycleChange"/>.
    /// </para></remarks>
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
        _running.TryRemove(target, out _);

        var stop = target.AppendAsync(async _ =>
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

                // Waited for, but not read: a stop runs at every state, Drained included. The null
                // check below already makes it a no-op once the drain has stopped the target, so the
                // only case a Drained check changes is the one it must not. StopAsync waits for the
                // stops queued before it, but one appended afterwards, by a graph move racing the
                // drain, is in no snapshot of either kind and reaches Drained still running.
                await _gate.WaitForOpenAsync().ConfigureAwait(false);

                var instance = target.Current;
                if (instance is null)
                {
                    return;
                }

                target.SetCurrent(null);

                await Task.Delay(StartDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

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
        }, cancellationToken);

        TrackInFlightStop(stop);
        return stop;
    }

    /// <summary>
    /// Registers a stop so <see cref="StopAsync"/> can be a barrier for it. A target leaves the
    /// running set when its stop is appended, so a stop queued before the drain is in no running set
    /// snapshot, and the host disposes the service provider as soon as the drain returns.
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
            // Detach runs inside a property write, so throwing here would surface at an unrelated assignment.
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
            .AppendAsync(_ => Task.CompletedTask, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target.Fault is { } fault)
        {
            throw fault;
        }

        // Read after the fault, and read at all because the guards above cannot cover a drain that
        // begins while this wait is queued behind the start: the start body then gates itself out and
        // sets nothing, and only the start body ever sets Current.
        return target.Current is not null;
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger ??= _loggerResolver();
        EnsureStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        // Snapshotted before anything else runs, so it holds exactly the stops that were queued
        // before the drain. Awaited below: without it the drain returns while one of them is still
        // touching a service provider the host disposes the moment this method returns.
        var queuedStops = _inFlightStops.Keys;

        if (DrainGate is { } drainGate)
        {
            await drainGate().ConfigureAwait(false);
        }

        // Liveness ends where the drain begins. A handler that still reports a subject as live claims
        // ownership of every attachment added to it afterwards, appends a start that no-ops, and never
        // releases it, because the release loop below only covers the drain's own snapshot.
        _liveSubjects.Clear();

        var snapshot = _running.ToArray();
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

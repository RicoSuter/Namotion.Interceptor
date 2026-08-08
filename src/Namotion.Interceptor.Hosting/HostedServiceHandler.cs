using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

// No longer IDisposable: the old implementation existed only to cancel the action loop's token
// source, and there is no such token under per target chains.
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
        // Invoked from inside LifecycleInterceptor's lock (_attachedSubjects). Everything here must
        // only append; appending never blocks and never runs user code.
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
            // A draining or drained handler must not take ownership again. Nothing it owns can ever
            // start, and the target would stay owned by a dead handler, so the next handler over the
            // same subject loses the compare and exchange and never starts anything.
            return;
        }

        _liveSubjects[subject] = 0;

        if (subject is IHostedService hostedService)
        {
            var target = subject.GetOrAddSubjectTarget(hostedService);
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target);
            }
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target);
            }
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
    /// Appends a start transition. Deliberately takes no cancellation token: a caller's token bounds
    /// its wait for the transition, never the transition itself, or cancelling an
    /// <c>AttachHostedServiceAsync</c> await would abort a start that is already under way and record
    /// the cancellation as a start failure.
    /// </summary>
    internal Task AppendStart(IInterceptorSubject subject, HostedServiceTarget target)
    {
        _running[target] = subject;

        return target.AppendAsync(async _ =>
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
        }, CancellationToken.None);
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

    internal Task EnsureStartedAsync()
    {
        _gate.EnsureStarted();
        return Task.CompletedTask;
    }

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
        return EnsureStartedAsync();
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

        await Task.WhenAll(stops).ConfigureAwait(false);

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

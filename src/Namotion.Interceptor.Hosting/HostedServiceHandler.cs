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
    // A workaround, not a design choice: docs/design/hosting-service-ownership.md#the-50-ms-delay.
    private const int TransitionDelayMilliseconds = 50;

    /// <summary>How long the drain waits between reads of the in flight count.</summary>
    private const int DrainPollMilliseconds = 1;

    private readonly HostedServiceGate _gate = new();
    private readonly ConcurrentDictionary<HostedServiceTarget, IInterceptorSubject> _owned = new();
    // Reference equality, as everywhere a subject is a key: two value equal subjects sharing one entry
    // means detaching either one clears the other's liveness while it is still in the graph.
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _liveSubjects =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The startup scope open in the flow that appends a start, or null. Ambient because the attach a
    /// constructing flow triggers runs inside a property write with nothing to pass a scope through.
    /// </summary>
    private readonly AsyncLocal<HostedServiceStartupScope?> _startupScope = new();

    internal HostedServiceStartupScope DeferStartup() => new(_startupScope);

    /// <summary>Drops the ambient scope for the calling flow, which for a body is its own copy.</summary>
    internal void ClearAmbientStartupScope() => _startupScope.Value = null;

    /// <summary>
    /// Transitions this handler appended that have not finished. Polled by the drain rather than
    /// signalled: docs/design/hosting-service-ownership.md#why-the-count-is-re-read-rather-than-signalled.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Set once by the service provider factory that hands this handler to the host. Volatile because
    /// the readers are transition and attaching threads with no ordering against that one, and a stale
    /// null here silently drops the errors this logger exists to report.
    /// </summary>
    private volatile ILogger? _logger;

    internal void SetLogger(ILogger logger) => _logger = logger;

    // The gates below are test seams, null in production.

    /// <summary>Awaited in <see cref="StopAsync"/> after the drain begins, before the liveness clear.</summary>
    internal Func<Task>? DrainGate { get; set; }

    /// <summary>Invoked after the take and before the gate re-read.</summary>
    internal Action? OwnershipTakenGate { get; set; }

    /// <summary>
    /// Invoked inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/> between the membership
    /// answer and the liveness write, so holding it holds the graph lock.
    /// </summary>
    internal Action? LivenessWriteGate { get; set; }

    /// <summary>Awaited in <see cref="StopAsync"/> between the owned snapshot and the stops it appends.</summary>
    internal Func<Task>? DrainAppendGate { get; set; }

    /// <summary>
    /// Awaited in <see cref="StopAsync"/> after the first wait for in flight transitions and before
    /// ownership is released.
    /// </summary>
    internal Func<Task>? DrainReleaseGate { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Runs inside LifecycleInterceptor's lock, so everything here only appends, which never blocks
        // and never runs a body. The one exception, TakeStartupHolds, is an accepted hazard: see
        // docs/design/hosting-service-ownership.md#4-a-deferrer-that-takes-a-lock-of-its-own.
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
        if (_gate.IsDraining)
        {
            return;
        }

        var subjectTarget = subject is IHostedService hostedService
            ? hostedService.GetOrAddSubjectTarget()
            : null;

        var attachments = subject.GetHostedServiceAttachments();
        if (subjectTarget is null && attachments.IsEmpty)
        {
            // Liveness is recorded only for a subject that hosts something: every reader of it holds
            // a target. MarkLiveIfAttached covers the one moment that is not true.
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

        if (_gate.IsDraining)
        {
            // Re-read after the write: an entry that lands after the drain cleared the set roots the
            // subject on a dead handler for the rest of that handler's life.
            _liveSubjects.TryRemove(subject, out _);
        }
    }

    private void DetachSubject(IInterceptorSubject subject)
    {
        // Read before allocating, because this runs for every subject a detach reaches. "Has ever
        // hosted", not "hosts now", for the reason on that method.
        var everHosted = subject.TryGetHostedServiceAttachments(out var attachments);
        var subjectTarget = subject is IHostedService ? subject.TryGetSubjectTarget() : null;
        if (subjectTarget is null && !everHosted)
        {
            return;
        }

        // Liveness is per subject. It cannot be per target: one subject reachable from two hosting
        // enabled contexts shares a single target with two handlers, and both are live for it while
        // only one of them owns it.
        _liveSubjects.TryRemove(subject, out _);

        if (subjectTarget is null && attachments.IsEmpty)
        {
            return;
        }

        // A handler stops what it owns and nothing else, or it disposes an instance another handler
        // created and is running. Decided inside the chain lock, with the append: read ahead of it, a
        // release landing between the two lets the stop escape the drain's barrier or reach an instance
        // the next owner has started since. Not readable from the transition body either, since
        // ownership is released just below and the body would always see a stranger. Appended now and
        // never deferred into another transition:
        // docs/design/hosting-service-ownership.md#why-a-composite-transition-is-wrong.
        TaskCompletionSource? subjectStopped = null;
        if (subjectTarget is not null)
        {
            // Allocated only when there is an attachment to order behind it: the signal exists to hold
            // the attachment stops until the subject's own stop returned, and nothing else reads it.
            var signal = attachments.IsEmpty
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Handed on only for an accepted append, for the reason on AppendStopIfOwned.
            if (AppendStopIfOwned(subject, subjectTarget, signal, waitFor: null, CancellationToken.None) is not null)
            {
                subjectStopped = signal;
            }
        }

        foreach (var attachment in attachments)
        {
            // A null wait is the "nothing to order behind" case. The stop body skips it, so that case
            // allocates no completed task to await.
            _ = AppendStopIfOwned(
                subject,
                ((IHostedServiceAttachmentTarget)attachment).Target,
                signal: null,
                waitFor: subjectStopped?.Task,
                CancellationToken.None);
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
        if (_gate.IsDraining)
        {
            // A draining or drained handler must not take ownership: a target left owned by a dead
            // handler makes every future handler over that subject lose the compare and exchange.
            // Read here as well as after the append, so the drained case installs no owner at all.
            return null;
        }

        // Taken before the append, never inside the body: a subsystem that treats "the graph has
        // finished starting" as a completion point would otherwise pass it while this start is still
        // queued.
        var startupHolds = TakeStartupHolds(subject.Context);

        // Read in the appending flow, which is the one that opened it. The body runs later and, on the
        // paths where a caller does not await the attach, in a flow that has already moved on.
        var startupScope = _startupScope.Value;

        var start = target.TryTakeOwnershipAndAppendAsync(
            this,
            subject,
            () => RunStartAsync(subject, target, startupHolds, startupScope),
            out var ownershipTaken);

        if (start is null)
        {
            ReleaseStartupHolds(startupHolds);
            return null;
        }

        OwnershipTakenGate?.Invoke();

        if (ownershipTaken && _gate.IsDraining)
        {
            // Re-read after both writes: they landed while the gate still read Running, so the drain's
            // snapshot covers this target, and any later read may have been swept past.
            //
            // A stop, not a plain retirement of the record: the start appended just above may already
            // be past every one of its guards and committed to creating an instance, which a retirement
            // would hide from every later snapshot. The stop is behind it on the same chain.
            AppendStop(subject, target, signal: null, waitFor: null, CancellationToken.None);

            // After the stop, never before, for the reason on the context detach path, and only for an
            // ownership this call installed: an earlier attach's may be running, and undoing that one
            // would pull it out of the set the drain is about to stop.
            target.ReleaseOwnership(this);
        }

        return start;
    }

    private async Task RunStartAsync(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        IDisposable[] startupHolds,
        HostedServiceStartupScope? startupScope)
    {
        try
        {
            await _gate.WaitForOpenAsync().ConfigureAwait(false);
            if (!MayStart(subject, target))
            {
                return;
            }

            if (startupScope is not null && !await WaitForConfigurationAsync(subject, target, startupScope).ConfigureAwait(false))
            {
                return;
            }

            // Cleared after every guard, never before: a start that is gated out or skipped must not
            // drop a fault that a caller has not read yet.
            target.ClearFault();

            try
            {
                // Entered here, past every guard and immediately before the factory: a refused start
                // must report the state it leaves behind rather than a start window it never entered.
                target.BeginStart();

                var instance = target.Subject ?? target.Factory!();
                if (target.IsHandlerOwnedInstance && !target.TryRecordFactoryInstance(instance))
                {
                    // Refused for every factory attachment, whatever the instance is. Why it fails
                    // closed: docs/design/hosting-service-ownership.md#faults-and-failed-starts.
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
                    // Stopped as well as disposed, because the generic host stops a service whose own
                    // StartAsync threw rather than treating it as never started, and a service that
                    // acquired a handle before throwing has nowhere else to release it: the instance is
                    // never recorded, so every later stop reads Current as null and returns.
                    await StopFailedStartAsync(instance, subject).ConfigureAwait(false);

                    if (target.IsHandlerOwnedInstance)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }

                    throw;
                }

                target.CompleteStart(instance);

                if (instance is BackgroundService { ExecuteTask: { } executeTask } backgroundService)
                {
                    ObserveExecution(subject, target, backgroundService, executeTask);
                }
            }
            catch (Exception exception)
            {
                target.SetStartFault(exception);
                _logger?.LogError(exception, "Failed to start hosted service for subject {Subject}.", subject);
            }
            finally
            {
                // Acts on a start that recorded nothing only: a successful one already left the start
                // window with the same write that recorded its instance.
                target.EndStart();
            }
        }
        finally
        {
            ReleaseStartupHolds(startupHolds);
        }
    }

    private static readonly Action<Task, object?> OnExecutionEnded =
        static (_, state) => ((ExecutionFaultObserver)state!).Append();

    /// <summary>
    /// Observes the execution a <see cref="BackgroundService"/> schedules from its start, which the
    /// start itself never covers. A fault or a cancellation in it is recorded on the target and the
    /// instance is stopped, so the target settles to <see cref="HostedServiceAttachmentState.Faulted"/>
    /// and the next context attach retries it.
    /// </summary>
    private void ObserveExecution(
        IInterceptorSubject subject, HostedServiceTarget target, BackgroundService instance, Task executeTask)
    {
        // A cancellation as well as a fault: an OperationCanceledException escaping the execution
        // leaves it Canceled whether or not a stop asked for it, and the identity check in the body is
        // what tells a stop's own cancellation apart. Never synchronous, because the continuation takes
        // the chain lock and the completing thread may hold anything.
        executeTask.ContinueWith(
            OnExecutionEnded,
            new ExecutionFaultObserver(this, subject, target, instance, executeTask),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>The state of one execution's fault continuation, and the transition it appends.</summary>
    private sealed class ExecutionFaultObserver(
        HostedServiceHandler handler,
        IInterceptorSubject subject,
        HostedServiceTarget target,
        BackgroundService instance,
        Task executeTask)
    {
        /// <summary>
        /// Appends the transition only while the handler still owns the target: after a release the
        /// instance is stopped or another handler's, and a stop this handler no longer counts would
        /// run past its own drain.
        /// </summary>
        public void Append()
        {
            if (target.AppendIfOwnedAsync(handler, RunAsync) is null)
            {
                try
                {
                    LogIgnoredFault();
                }
                catch (Exception)
                {
                    // Outside the chain's catch-all, so a throwing log provider would fault this dropped continuation.
                }
            }
        }

        private async Task RunAsync()
        {
            // The run that ended must still be the one recorded: a subject is restarted in place on the
            // same instance with a new execute task, and a stop ahead on the chain leaves Current null,
            // so neither check on its own tells this run from the next. Every stop leaves Current
            // before it calls StopAsync, so a run its own stop cancelled never passes this.
            if (!ReferenceEquals(target.Current, instance) || !ReferenceEquals(instance.ExecuteTask, executeTask))
            {
                LogIgnoredFault();
                return;
            }

            var fault = ReadFault(executeTask);

            target.SetFault(fault);
            handler._logger?.LogError(fault, "Hosted service for subject {Subject} faulted while running.", subject);

            await handler.CreateStopBody(subject, target, signal: null, waitFor: null, CancellationToken.None)()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Logs the fault of a run that was already stopped or replaced, which also observes it. A
        /// cancellation there is the stop's own and is not logged.
        /// </summary>
        private void LogIgnoredFault()
        {
            if (executeTask.IsFaulted)
            {
                var fault = ReadFault(executeTask);
                handler._logger?.LogDebug(
                    fault, "Hosted service for subject {Subject} faulted after it was stopped or restarted; ignored.", subject);
            }
        }

        private static Exception ReadFault(Task executeTask)
        {
            if (executeTask.Exception is { } exception)
            {
                return exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;
            }

            // Canceled, where Exception is null and awaiting rethrows the exception the execution raised.
            try
            {
                executeTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException cancellation)
            {
                return cancellation;
            }

            return new TaskCanceledException(executeTask);
        }
    }

    /// <summary>Whether a start body may still create an instance, read before the scope wait and after it.</summary>
    private bool MayStart(IInterceptorSubject subject, HostedServiceTarget target)
    {
        // The gate inside the body, never at append time: a start queued when shutdown begins has to
        // re-read, and a body skipped at append time would never run its signalling.
        //
        // Liveness and ownership are two windows, and neither is redundant: an explicit detach retires
        // the record without releasing, so only liveness refuses a body behind it, and a body that
        // reads liveness after a later attach rebuilt it is refused only by ownership.
        //
        // One instance per target, in the body where the chain serializes the two starts. Ownership does
        // not cover this: the owning handler sees a context attach per context and appends a start for
        // each.
        return _gate.State == HostedServiceGateState.Running
            && _liveSubjects.ContainsKey(subject)
            && ReferenceEquals(target.Owner, this)
            && target.Current is null;
    }

    /// <summary>Waits for the startup scope this start was captured in and reports whether it may still run.</summary>
    /// <remarks>
    /// Every guard the caller read before this is re-read after it: a scope holds a start for as long
    /// as its flow stays open, and the subject can leave the graph in that time. The drain releases the
    /// wait as well as the scope does, or a scope nobody disposes would hold the drain's barrier for
    /// the whole shutdown deadline.
    /// </remarks>
    private async Task<bool> WaitForConfigurationAsync(
        IInterceptorSubject subject, HostedServiceTarget target, HostedServiceStartupScope startupScope)
    {
        if (!startupScope.IsReady)
        {
            await Task.WhenAny(
                    startupScope.WaitAsync(CancellationToken.None),
                    _gate.WaitForDrainingAsync())
                .ConfigureAwait(false);
        }

        return MayStart(subject, target);
    }

    /// <summary>Takes a completion hold on every deferrer reachable from <paramref name="context"/>.</summary>
    /// <remarks>
    /// The constraint this call site puts on an implementer is on
    /// <see cref="IStartupCompletionDeferrer"/>.
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
                _logger?.LogError(exception, "Taking a startup completion hold threw and was ignored.");
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
                _logger?.LogError(exception, "Releasing a startup completion hold threw and was ignored.");
            }
        }
    }

    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.AppendAsync(this, CreateStopBody(subject, target, signal, waitFor, cancellationToken));

    /// <summary>
    /// Appends a stop only while this handler still owns the target, the two decided under one
    /// acquisition of the chain lock. Returns null when the append was refused, which the caller must
    /// not then hand to another stop as a wait: a refused stop has no body, so nothing sets its signal.
    /// </summary>
    private Task? AppendStopIfOwned(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.AppendIfOwnedAsync(this, CreateStopBody(subject, target, signal, waitFor, cancellationToken));

    private Func<Task> CreateStopBody(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => async () =>
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

                // Waited for, but not read: a stop runs at every state, after the drain included, or
                // one appended after the drain snapshotted everything never disposes its instance. The
                // null check below is what makes a stop idempotent.
                await _gate.WaitForOpenAsync().ConfigureAwait(false);

                var instance = target.Current;
                if (instance is null)
                {
                    return;
                }

                // Below the return above, so a stop with nothing to do reports no window of its own.
                target.BeginStop();

                // A cancelled token skips the delay, but the stop and the dispose below are still owed.
                await Task.Delay(TransitionDelayMilliseconds, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                try
                {
                    await instance.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caught rather than filtered out, and not recorded as a fault: a cancelled stop
                    // is the caller's token expiring, not a failure, but the dispose below still has
                    // to run, and Current is already cleared so an escape leaks the instance.
                }
                catch (Exception exception)
                {
                    target.SetFault(exception);
                    _logger?.LogError(exception, "Failed to stop hosted service for subject {Subject}.", subject);
                }
                finally
                {
                    // In the finally rather than after the catch: Current is already cleared, so an
                    // escape from anything above would leave the instance reachable from nothing.
                    if (target.IsHandlerOwnedInstance)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Left ahead of the signal, so nothing ordered behind it reads this target still in
                // its stop window.
                target.EndStop();

                // Always signals, including on the gated-out and cancelled paths, or a paired
                // attachment stop parks forever on a signal that is never set.
                signal?.TrySetResult();
            }
        };

    /// <summary>
    /// Stops an instance whose own start threw, reporting a failure here rather than raising it.
    /// </summary>
    /// <remarks>
    /// Contains its own failure for the reason on <see cref="DisposeInstanceAsync"/>: this runs inside a
    /// catch that rethrows the start's exception, which is the one a caller waits for, and an escape
    /// from here would replace it and skip the dispose behind it.
    /// </remarks>
    private async Task StopFailedStartAsync(IHostedService instance, IInterceptorSubject subject)
    {
        try
        {
            await instance.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to stop hosted service for subject {Subject} after its start failed.", subject);
        }
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
            // Contains as well as reports: the cleanup dispose in RunStartAsync runs inside a catch
            // that rethrows the start's own exception, and an escape from here skips that rethrow.
            _logger?.LogError(exception, "Failed to dispose hosted service {Service}.", instance.ToString());
        }
    }

    /// <summary>Opens the startup gate if it has never been opened, and does nothing afterwards.</summary>
    internal void EnsureStarted() => _gate.EnsureStarted();

    /// <summary>
    /// Waits for the start this handler appended for the subject and rethrows the fault it recorded,
    /// so a subject that fails to start aborts host startup the way <c>AddHostedService</c> does.
    /// Returns false when nothing was started, which the caller must not read as a start.
    /// </summary>
    internal async Task<bool> WaitForStartAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        // Reads the target and never creates one, and never takes ownership: a drain releases only
        // what its own snapshot held, so a claim taken here would never be released and the next
        // handler over the same subject would lose the compare and exchange forever.
        var target = subject.TryGetSubjectTarget();
        if (target is null || !_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
        {
            return false;
        }

        // An empty transition on the same chain. Appending never runs a body, so this completes only
        // once the start appended ahead of it has run.
        await target
            .AppendAsync(this, () => Task.CompletedTask)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (target.StartFault is { } fault)
        {
            // Captured rather than rethrown, for the reason on AttachHostedServiceAsync.
            ExceptionDispatchInfo.Throw(fault);
        }

        // Read at all because the guards above cannot cover a drain beginning while this wait is
        // queued behind the start: the start body then gates itself out and sets nothing.
        return target.Current is not null;
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    /// <summary>
    /// Records a target this handler installed itself as the owner of, so its drain can stop and
    /// release it. Written from the take, on the install only: a repeat take finds a record an earlier
    /// take made, and undoing that one would pull a running instance out of the drain's snapshot.
    /// </summary>
    internal void RecordOwnership(HostedServiceTarget target, IInterceptorSubject subject) => _owned[target] = subject;

    /// <summary>
    /// Retires a target's record. Called by the release, and by an explicit detach, which stops a
    /// target without releasing it and inherits the rule that it must still retire the record.
    /// </summary>
    internal void ForgetOwnership(HostedServiceTarget target) => _owned.TryRemove(target, out _);

    internal void EnterTransition() => Interlocked.Increment(ref _inFlight);

    internal void LeaveTransition() => Interlocked.Decrement(ref _inFlight);

    /// <summary>How many transitions this handler has appended that have not finished. Test only.</summary>
    internal int InFlightTransitionCount => Volatile.Read(ref _inFlight);

    /// <summary>Whether this handler holds the target in the set its drain would stop. Test only.</summary>
    internal bool IsOwned(HostedServiceTarget target) => _owned.ContainsKey(target);

    /// <summary>
    /// Records liveness for a subject already in the graph that hosted nothing when it entered, so
    /// <c>AttachSubject</c> recorded none. The one moment the answer cannot be taken from a target.
    /// </summary>
    /// <remarks>
    /// The write must stay inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback.
    /// Reading membership and then writing releases the lock in between, and a graph move landing in
    /// that gap makes the write land on the opposite answer. The take after it does not need the same
    /// treatment, because it reads liveness under the chain lock and refuses on its own. What asking
    /// every reachable interceptor costs is in
    /// docs/design/hosting-service-ownership.md#a-handler-can-be-marked-live-for-a-graph-it-does-not-serve.
    /// </remarks>
    internal void MarkLiveIfAttached(IInterceptorSubject subject)
    {
        if (_gate.IsDraining)
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

        if (recorded && _gate.IsDraining)
        {
            // Re-read after the write, for the reason in AttachSubject.
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

        var snapshot = _owned.ToArray();

        if (DrainAppendGate is { } drainAppendGate)
        {
            await drainAppendGate().ConfigureAwait(false);
        }

        // The same shape per owned subject as a context detach: a subject's own stop has to return
        // before the attachments it uses are stopped and disposed underneath it.
        Dictionary<IInterceptorSubject, TaskCompletionSource>? subjectStops = null;
        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is null)
            {
                continue;
            }

            var subjectStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (AppendStopIfOwned(subject, target, subjectStopped, waitFor: null, cancellationToken) is null)
            {
                continue;
            }

            // Recorded only for an accepted append, for the reason on AppendStopIfOwned.
            (subjectStops ??= new Dictionary<IInterceptorSubject, TaskCompletionSource>(ReferenceEqualityComparer.Instance))[subject] = subjectStopped;
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

            // Discarded rather than collected: the count is what the drain waits on, and a stop this
            // append refused is one another handler now owns.
            _ = AppendStopIfOwned(subject, target, signal: null, waitFor, cancellationToken);
        }

        // Bounded by the token, which for a host is the shutdown deadline. Nothing further down
        // observes it: the chain waits inside a stop body are untokened by design, and a stop wedged
        // behind one of them would otherwise hold the process open forever.
        var remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);

        if (DrainReleaseGate is { } drainReleaseGate)
        {
            await drainReleaseGate().ConfigureAwait(false);
        }

        foreach (var (target, _) in snapshot)
        {
            // After the stops, so a second host cannot start ahead of this host's stop, and even when
            // the wait above gave up: a wrong stop is recoverable and a target owned by a dead handler
            // is not.
            target.ReleaseOwnership(this);
        }

        if (remaining == 0)
        {
            // Read again rather than held: an append landing after the count first reached zero went
            // through the same increment, and only a second read sees it.
            remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (remaining != 0)
        {
            // Logged rather than thrown, so the release above still runs: rethrowing would leave every
            // target owned by a dead handler and a second host over the same subjects starting nothing.
            _logger?.LogWarning(
                "Shutdown gave up waiting for {Count} hosted service transitions; they keep running unobserved.",
                remaining);
        }

        // The owned set is not cleared here. After the release loop the only entries left are installs
        // whose own gate re-read releases them, and clearing is what would make the set and the owner
        // field disagree for anything still in flight.
    }

    /// <summary>
    /// Waits for every transition this handler appended to finish, and reports how many were still
    /// running when <paramref name="cancellationToken"/> expired. Zero means the wait completed.
    /// </summary>
    private async Task<int> WaitForTransitionsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _inFlight);
            if (remaining == 0)
            {
                return 0;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return remaining;
            }

            // Untokened, so every round reads the count and the deadline in the same order.
            await Task.Delay(DrainPollMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

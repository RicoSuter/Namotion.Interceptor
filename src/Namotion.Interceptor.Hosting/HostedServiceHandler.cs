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
    /// Set once by the service provider factory. Volatile because the readers have no ordering against
    /// that write, and a stale null silently drops the errors this logger exists to report.
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

        // Stops only what this handler owns, decided with the append under the chain lock: ahead of
        // it, a release in between lets the stop escape the drain or reach a stranger's instance; in
        // the body, the release below has already run. Appended now, never deferred into a transition:
        // docs/design/hosting-service-ownership.md#why-a-composite-transition-is-wrong.
        if (subjectTarget is not null)
        {
            _ = AppendStopIfOwned(subject, subjectTarget, waitFor: null, CancellationToken.None);
        }

        foreach (var attachment in attachments)
        {
            _ = AppendStopIfOwned(
                subject,
                ((IHostedServiceAttachmentTarget)attachment).Target,
                waitFor: subjectTarget?.GetStopToAwait(this),
                CancellationToken.None);
        }

        // After the stops are appended and never from a body:
        // docs/design/hosting-service-ownership.md#ownership-is-released-on-context-detach-and-on-drain.
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
            // Read here as well as after the append, so a drained handler installs no owner at all.
            return null;
        }

        // Before the append, never inside the body, or "the graph has finished starting" is reachable
        // while this start is still queued.
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
            // Undoes a take the drain's snapshot may have missed, and only one this call installed. A
            // stop rather than a bare retirement, because the start above may already be committed:
            // docs/design/hosting-service-ownership.md#why-the-ownership-decision-is-inside-the-chain-lock.
            // An attachment's undo stop waits for its subject's stop like every other attachment stop,
            // and the release below makes this the only stop the drain can get onto that chain.
            AppendStop(subject, target, waitFor: SubjectStopToAwait(subject, target), CancellationToken.None);
            target.ReleaseOwnership(this);
        }

        return start;
    }

    /// <summary>
    /// The subject stop an attachment stop appended now has to wait for, or null for a subject target
    /// and for an attachment whose subject has no stop of this handler's pending.
    /// </summary>
    private Task? SubjectStopToAwait(IInterceptorSubject subject, HostedServiceTarget target)
        => target.Subject is null && subject is IHostedService
            ? subject.TryGetSubjectTarget()?.GetStopToAwait(this)
            : null;

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
        // A cancellation as well as a fault, since the execution is Canceled whether or not a stop asked
        // for it. Never synchronous: the continuation takes the chain lock on a thread that may hold anything.
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
            // Both checks: a subject restarts in place on the same instance with a new execute task, and
            // every stop leaves Current before StopAsync, so a run its own stop cancelled never passes.
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
        // Four guards, none covered by another, each re-read here because the append happened earlier:
        // docs/design/hosting-service-ownership.md#the-read-inside-the-start-body. The last one is what
        // stops the owning handler's second start for a subject reachable from two contexts:
        // docs/design/hosting-service-ownership.md#ownership-is-not-what-makes-two-contexts-over-one-subject-benign.
        return _gate.State == HostedServiceGateState.Running
            && _liveSubjects.ContainsKey(subject)
            && ReferenceEquals(target.Owner, this)
            && target.Current is null;
    }

    /// <summary>
    /// Waits for the startup scope this start was captured in, or for the drain, and re-reads every
    /// guard afterwards, because the subject can leave the graph while the scope is open.
    /// </summary>
    private async Task<bool> WaitForConfigurationAsync(
        IInterceptorSubject subject, HostedServiceTarget target, HostedServiceStartupScope startupScope)
    {
        if (!startupScope.IsReady)
        {
            await Task.WhenAny(startupScope.WaitAsync(), _gate.WaitForDrainingAsync()).ConfigureAwait(false);
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

    /// <summary>
    /// Appends a stop. A subject target's stop carries the target's stop signal, chosen with the append;
    /// an attachment's stop waits for <paramref name="waitFor"/>, its subject's stop, when there is one.
    /// </summary>
    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.Subject is null
            ? target.AppendAsync(this, CreateStopBody(subject, target, signal: null, waitFor, cancellationToken))
            : target.AppendSubjectStopAsync(this, signal => CreateStopBody(subject, target, signal, waitFor, cancellationToken));

    /// <summary>
    /// <see cref="AppendStop"/>, only while this handler still owns the target, the two decided under
    /// one acquisition of the chain lock. Returns null when the append was refused.
    /// </summary>
    private Task? AppendStopIfOwned(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        Task? waitFor,
        CancellationToken cancellationToken)
        => target.Subject is null
            ? target.AppendIfOwnedAsync(this, CreateStopBody(subject, target, signal: null, waitFor, cancellationToken))
            : target.AppendSubjectStopIfOwnedAsync(this, signal => CreateStopBody(subject, target, signal, waitFor, cancellationToken));

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

                // Always signals, including on the cancelled path, or an attachment stop ordered behind
                // this one parks forever on a signal that is never set.
                signal?.TrySetResult();
            }
        };

    /// <summary>
    /// Stops an instance whose own start threw. Contains its own failure for the reason on
    /// <see cref="DisposeInstanceAsync"/>.
    /// </summary>
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
        // Never creates a target and never takes ownership: a claim taken here would never be released.
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

    /// <summary>Records a target this handler installed itself as the owner of, so its drain can stop and release it.</summary>
    internal void RecordOwnership(HostedServiceTarget target, IInterceptorSubject subject) => _owned[target] = subject;

    /// <summary>Retires a target's record. Called by the release, and by an explicit detach, which stops without releasing.</summary>
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
        // before the attachments it uses are stopped and disposed underneath it. Subject stops first,
        // so each attachment stop finds its subject's signal already chosen rather than creating it.
        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is not null)
            {
                _ = AppendStopIfOwned(subject, target, waitFor: null, cancellationToken);
            }
        }

        foreach (var (target, subject) in snapshot)
        {
            if (target.Subject is null)
            {
                // Discarded rather than collected: the count is what the drain waits on, and a stop
                // this append refused is one another handler now owns.
                _ = AppendStopIfOwned(subject, target, SubjectStopToAwait(subject, target), cancellationToken);
            }
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

        // The owned set is not cleared: what is left belongs to installs whose own gate re-read releases them.
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

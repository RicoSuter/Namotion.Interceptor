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
    private readonly ConcurrentDictionary<HostedServiceSlot, IInterceptorSubject> _stopOnDrain = new();
    // Reference equality, as everywhere a subject is a key: two value equal subjects sharing one entry
    // means detaching either one clears the other's liveness while it is still in the graph.
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _liveSubjects =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The start deferral open in the flow that enqueues a start, or null. Ambient because the attach a
    /// constructing flow triggers runs inside a property write with nothing to pass a deferral through.
    /// </summary>
    private readonly AsyncLocal<HostedServiceStartDeferral?> _startDeferral = new();

    internal HostedServiceStartDeferral DeferStarts() => new(_startDeferral);

    /// <summary>Drops the ambient start deferral for the calling flow, which for a body is its own copy.</summary>
    internal void ClearAmbientStartDeferral() => _startDeferral.Value = null;

    /// <summary>
    /// Transitions this handler enqueued that have not finished. Polled by the drain rather than
    /// signalled: docs/design/hosting-service-ownership.md#why-the-count-is-re-read-rather-than-signalled.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Set once by the service provider factory. Volatile because the readers have no ordering against
    /// that write, and a stale null silently drops the errors this logger exists to report.
    /// </summary>
    private volatile ILogger? _logger;

    internal void SetLogger(ILogger logger) => _logger = logger;

    // The hooks below are test seams, null in production.

    /// <summary>Awaited in <see cref="StopAsync"/> after the drain begins, before the liveness clear.</summary>
    internal Func<Task>? DrainTestHook { get; set; }

    /// <summary>Invoked after the take and before the gate re-read.</summary>
    internal Action? OwnershipTakenTestHook { get; set; }

    /// <summary>
    /// Invoked inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/> between the membership
    /// answer and the liveness write, so holding it holds the graph lock.
    /// </summary>
    internal Action? LivenessWriteTestHook { get; set; }

    /// <summary>Awaited in <see cref="StopAsync"/> between the drain snapshot and the stops it enqueues.</summary>
    internal Func<Task>? DrainEnqueueTestHook { get; set; }

    /// <summary>
    /// Awaited in <see cref="StopAsync"/> after the first wait for in flight transitions and before
    /// ownership is released.
    /// </summary>
    internal Func<Task>? DrainReleaseTestHook { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Runs inside LifecycleInterceptor's lock, so everything here only enqueues, which never blocks
        // and never runs a body. The one exception, DeferStartupCompletion, is an accepted hazard: see
        // docs/design/hosting-service-ownership.md#4-a-startup-completion-that-takes-a-lock-of-its-own.
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

        var subjectSlot = subject is IHostedService hostedService
            ? hostedService.GetOrAddSubjectSlot()
            : null;

        var attachments = subject.GetHostedServiceAttachments();
        if (subjectSlot is null && attachments.IsEmpty)
        {
            // Liveness is recorded only for a subject that hosts something: every reader of it holds
            // a slot. MarkLiveIfAttached covers the one moment that is not true.
            return;
        }

        _liveSubjects[subject] = 0;

        if (subjectSlot is not null)
        {
            TryTakeOwnershipAndStart(subject, subjectSlot);
        }

        foreach (var attachment in attachments)
        {
            TryTakeOwnershipAndStart(subject, ((IHostedServiceSlotAccess)attachment).Slot);
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
        var subjectSlot = subject is IHostedService ? subject.TryGetSubjectSlot() : null;
        if (subjectSlot is null && !everHosted)
        {
            return;
        }

        // Liveness is per subject. It cannot be per slot: one subject reachable from two hosting
        // enabled contexts shares a single slot with two handlers, and both are live for it while
        // only one of them owns it.
        _liveSubjects.TryRemove(subject, out _);

        // Stops only what this handler owns, decided with the enqueue under the queue lock: ahead of
        // it, a release in between lets the stop escape the drain or reach a stranger's instance; in
        // the body, the release below has already run. Enqueued now, never deferred into a transition:
        // docs/design/hosting-service-ownership.md#why-a-composite-transition-is-wrong.
        if (subjectSlot is not null)
        {
            _ = EnqueueStopIfOwned(subject, subjectSlot, waitFor: null, CancellationToken.None);
        }

        foreach (var attachment in attachments)
        {
            _ = EnqueueStopIfOwned(
                subject,
                ((IHostedServiceSlotAccess)attachment).Slot,
                waitFor: subjectSlot?.GetStopToAwait(this),
                CancellationToken.None);
        }

        // After the stops are enqueued and never from a body:
        // docs/design/hosting-service-ownership.md#ownership-is-released-on-context-detach-and-on-drain.
        subjectSlot?.ReleaseOwnership(this);
        foreach (var attachment in attachments)
        {
            ((IHostedServiceSlotAccess)attachment).Slot.ReleaseOwnership(this);
        }
    }

    /// <summary>
    /// Takes ownership of the slot for this handler and enqueues its start, returning the enqueued
    /// transition. Returns null when the handler took nothing, because the subject is no longer live
    /// for it, because another handler owns the slot, or because this handler is draining.
    /// </summary>
    /// <remarks>
    /// The transition carries no cancellation token: a caller's token bounds its wait, never the
    /// transition, or cancelling an <c>AttachHostedServiceAsync</c> await would abort a start already
    /// under way and record it as a failure.
    /// </remarks>
    internal Task? TryTakeOwnershipAndStart(IInterceptorSubject subject, HostedServiceSlot slot)
    {
        if (_gate.IsDraining)
        {
            // Read here as well as after the enqueue, so a drained handler installs no owner at all.
            return null;
        }

        // Before the enqueue, never inside the body, or "the graph has finished starting" is reachable
        // while this start is still queued.
        var completionDeferrals = DeferStartupCompletion(subject.Context);

        // Read in the enqueuing flow, which is the one that opened it. The body runs later and, on the
        // paths where a caller does not await the attach, in a flow that has already moved on.
        var startDeferral = _startDeferral.Value;

        var start = slot.TryTakeOwnershipAndEnqueueAsync(
            this,
            subject,
            () => RunStartAsync(subject, slot, completionDeferrals, startDeferral),
            out var ownershipTaken);

        if (start is null)
        {
            ReleaseCompletionDeferrals(completionDeferrals);
            return null;
        }

        OwnershipTakenTestHook?.Invoke();

        if (ownershipTaken && _gate.IsDraining)
        {
            // Undoes a take the drain's snapshot may have missed, and only one this call installed. A
            // stop rather than a bare retirement, because the start above may already be committed:
            // docs/design/hosting-service-ownership.md#why-the-ownership-decision-is-inside-the-queue-lock.
            // An attachment's undo stop waits for its subject's stop like every other attachment stop,
            // and the release below makes this the only stop the drain can get onto that queue. Only
            // while still owned: the drain can finish and another handler take the slot before this
            // runs, and every release that can take this ownership first has already enqueued a stop
            // behind the start.
            _ = EnqueueStopIfOwned(subject, slot, waitFor: SubjectStopToAwait(subject, slot), CancellationToken.None);
            slot.ReleaseOwnership(this);
        }

        return start;
    }

    /// <summary>
    /// The subject stop an attachment stop enqueued now has to wait for, or null for a subject slot
    /// and for an attachment whose subject has no stop of this handler's pending.
    /// </summary>
    private Task? SubjectStopToAwait(IInterceptorSubject subject, HostedServiceSlot slot)
        => slot.Subject is null && subject is IHostedService
            ? subject.TryGetSubjectSlot()?.GetStopToAwait(this)
            : null;

    private async Task RunStartAsync(
        IInterceptorSubject subject,
        HostedServiceSlot slot,
        IDisposable[] completionDeferrals,
        HostedServiceStartDeferral? startDeferral)
    {
        try
        {
            await _gate.WaitForOpenAsync().ConfigureAwait(false);
            if (!MayStart(subject, slot))
            {
                return;
            }

            if (startDeferral is not null && !await WaitForStartDeferralAsync(subject, slot, startDeferral).ConfigureAwait(false))
            {
                return;
            }

            // Cleared after every guard, never before: a start that is gated out or skipped must not
            // drop a fault that a caller has not read yet.
            slot.ClearFault();

            try
            {
                // Entered here, past every guard and immediately before the factory: a refused start
                // must report the state it leaves behind rather than a start window it never entered.
                slot.BeginStart();

                var instance = slot.Subject ?? slot.Factory!();
                if (slot.IsFactoryAttachment && !slot.TryRecordFactoryInstance(instance))
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

                    if (slot.IsFactoryAttachment)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }

                    throw;
                }

                slot.RecordStarted(instance);

                if (instance is BackgroundService { ExecuteTask: { } executeTask } backgroundService)
                {
                    ObserveExecution(subject, slot, backgroundService, executeTask);
                }
            }
            catch (Exception exception)
            {
                slot.SetStartFault(exception);
                _logger?.LogError(exception, "Failed to start hosted service for subject {Subject}.", subject);
            }
            finally
            {
                // Acts on a start that recorded nothing only: a successful one already left the start
                // window with the same write that recorded its instance.
                slot.AbandonStart();
            }
        }
        finally
        {
            ReleaseCompletionDeferrals(completionDeferrals);
        }
    }

    private static readonly Action<Task, object?> OnExecutionEnded =
        static (_, state) => ((ExecutionFaultObserver)state!).Enqueue();

    /// <summary>
    /// Observes the execution a <see cref="BackgroundService"/> schedules from its start, which the
    /// start itself never covers. A fault or a cancellation in it is recorded on the slot and the
    /// instance is stopped, so the slot settles to <see cref="HostedServiceAttachmentState.Faulted"/>
    /// and the next context attach retries it.
    /// </summary>
    private void ObserveExecution(
        IInterceptorSubject subject, HostedServiceSlot slot, BackgroundService instance, Task executeTask)
    {
        // A cancellation as well as a fault, since the execution is Canceled whether or not a stop asked
        // for it. Never synchronous: the continuation takes the queue lock on a thread that may hold anything.
        executeTask.ContinueWith(
            OnExecutionEnded,
            new ExecutionFaultObserver(this, subject, slot, instance, executeTask),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>The state of one execution's fault continuation, and the transition it enqueues.</summary>
    private sealed class ExecutionFaultObserver(
        HostedServiceHandler handler,
        IInterceptorSubject subject,
        HostedServiceSlot slot,
        BackgroundService instance,
        Task executeTask)
    {
        /// <summary>
        /// Enqueues the transition only while the handler still owns the slot: after a release the
        /// instance is stopped or another handler's, and a stop this handler no longer counts would
        /// run past its own drain.
        /// </summary>
        public void Enqueue()
        {
            if (slot.EnqueueIfOwnedAsync(handler, RunAsync) is null)
            {
                try
                {
                    LogIgnoredFault();
                }
                catch (Exception)
                {
                    // Outside the queue's catch-all, so a throwing log provider would fault this dropped continuation.
                }
            }
        }

        private async Task RunAsync()
        {
            // Both checks: a subject restarts in place on the same instance with a new execute task, and
            // every stop leaves Current before StopAsync, so a run its own stop cancelled never passes.
            if (!ReferenceEquals(slot.Current, instance) || !ReferenceEquals(instance.ExecuteTask, executeTask))
            {
                LogIgnoredFault();
                return;
            }

            var fault = ReadFault(executeTask);

            slot.SetFault(fault);
            handler._logger?.LogError(fault, "Hosted service for subject {Subject} faulted while running.", subject);

            await handler.CreateStopBody(subject, slot, signal: null, waitFor: null, CancellationToken.None)()
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

    /// <summary>Whether a start body may still create an instance, read before the deferral wait and after it.</summary>
    private bool MayStart(IInterceptorSubject subject, HostedServiceSlot slot)
    {
        // Four guards, none covered by another, each re-read here because the enqueue happened earlier:
        // docs/design/hosting-service-ownership.md#the-read-inside-the-start-body. The last one is what
        // stops the owning handler's second start for a subject reachable from two contexts:
        // docs/design/hosting-service-ownership.md#ownership-is-not-what-makes-two-contexts-over-one-subject-benign.
        return _gate.State == HostedServiceGateState.Open
            && _liveSubjects.ContainsKey(subject)
            && ReferenceEquals(slot.Owner, this)
            && slot.Current is null;
    }

    /// <summary>
    /// Waits for the start deferral this start was captured in, or for the drain, and re-reads every
    /// guard afterwards, because the subject can leave the graph while the deferral is open.
    /// </summary>
    private async Task<bool> WaitForStartDeferralAsync(
        IInterceptorSubject subject, HostedServiceSlot slot, HostedServiceStartDeferral startDeferral)
    {
        if (!startDeferral.IsReady)
        {
            await Task.WhenAny(startDeferral.WaitAsync(), _gate.WaitForDrainingAsync()).ConfigureAwait(false);
        }

        return MayStart(subject, slot);
    }

    /// <summary>Defers startup completion on every startup completion reachable from <paramref name="context"/>.</summary>
    /// <remarks>
    /// The constraint this call site puts on an implementer is on
    /// <see cref="IStartupCompletion"/>.
    /// </remarks>
    private IDisposable[] DeferStartupCompletion(IInterceptorSubjectContext context)
    {
        var startupCompletions = context.GetServices<IStartupCompletion>();
        if (startupCompletions.IsEmpty)
        {
            return [];
        }

        var completionDeferrals = new IDisposable[startupCompletions.Length];
        var deferredCount = 0;
        foreach (var startupCompletion in startupCompletions)
        {
            try
            {
                completionDeferrals[deferredCount] = startupCompletion.Defer();
                deferredCount++;
            }
            catch (Exception exception)
            {
                // One startup completion throwing must not abandon the completion deferrals already
                // taken, and must not propagate: an attach runs under the lifecycle lock inside a
                // property write, so the exception would surface at an unrelated assignment.
                _logger?.LogError(exception, "Deferring startup completion threw and was ignored.");
            }
        }

        return deferredCount == completionDeferrals.Length ? completionDeferrals : completionDeferrals[..deferredCount];
    }

    private void ReleaseCompletionDeferrals(IDisposable[] completionDeferrals)
    {
        foreach (var completionDeferral in completionDeferrals)
        {
            try
            {
                completionDeferral.Dispose();
            }
            catch (Exception exception)
            {
                // One startup completion throwing must not strand the others, for the same reason the
                // start body releases its completion deferrals in a finally at all.
                _logger?.LogError(exception, "Releasing a completion deferral threw and was ignored.");
            }
        }
    }

    /// <summary>
    /// Enqueues an attachment's stop whoever owns its slot, for an explicit detach.
    /// </summary>
    internal Task EnqueueAttachmentStop(IInterceptorSubject subject, HostedServiceSlot slot, CancellationToken cancellationToken)
        => slot.EnqueueAsync(this, CreateStopBody(subject, slot, signal: null, waitFor: null, cancellationToken));

    /// <summary>
    /// Enqueues a stop only while this handler still owns the slot, the two decided under one
    /// acquisition of the queue lock. A subject slot's stop carries the slot's stop signal, chosen
    /// with the enqueue; an attachment's stop waits for <paramref name="waitFor"/>, its subject's stop,
    /// when there is one. Returns null when the enqueue was refused.
    /// </summary>
    private Task? EnqueueStopIfOwned(
        IInterceptorSubject subject,
        HostedServiceSlot slot,
        Task? waitFor,
        CancellationToken cancellationToken)
        => slot.Subject is null
            ? slot.EnqueueIfOwnedAsync(this, CreateStopBody(subject, slot, signal: null, waitFor, cancellationToken))
            : slot.EnqueueSubjectStopIfOwnedAsync(this, signal => CreateStopBody(subject, slot, signal, waitFor, cancellationToken));

    private Func<Task> CreateStopBody(
        IInterceptorSubject subject,
        HostedServiceSlot slot,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
        => async () =>
        {
            try
            {
                if (waitFor is not null)
                {
                    // Orders a subject's stop ahead of its attachments. Acyclic: the subject's queue
                    // waits on nothing. A hosted service must therefore not detach an attachment from
                    // inside its own stop path, or this becomes a cycle.
                    await waitFor.ConfigureAwait(false);
                }

                // Waited for, but not read: a stop runs at every state, after the drain included, or
                // one enqueued after the drain snapshotted everything never disposes its instance. The
                // null check below is what makes a stop idempotent.
                await _gate.WaitForOpenAsync().ConfigureAwait(false);

                var instance = slot.Current;
                if (instance is null)
                {
                    return;
                }

                // Below the return above, so a stop with nothing to do reports no window of its own.
                slot.BeginStop();

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
                    slot.SetFault(exception);
                    _logger?.LogError(exception, "Failed to stop hosted service for subject {Subject}.", subject);
                }
                finally
                {
                    // In the finally rather than after the catch: Current is already cleared, so an
                    // escape from anything above would leave the instance reachable from nothing.
                    if (slot.IsFactoryAttachment)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Left ahead of the signal, so nothing ordered behind it reads this slot still in
                // its stop window.
                slot.EndStop();

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
    internal void OpenGate() => _gate.Open();

    /// <summary>
    /// Waits for the start this handler enqueued for the subject and rethrows the fault it recorded,
    /// so a subject that fails to start aborts host startup the way <c>AddHostedService</c> does.
    /// Returns false when nothing was started, which the caller must not read as a start.
    /// </summary>
    internal async Task<bool> WaitForStartAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        // Never creates a slot and never takes ownership: a claim taken here would never be released.
        var slot = subject.TryGetSubjectSlot();
        if (slot is null || !_liveSubjects.ContainsKey(subject) || !ReferenceEquals(slot.Owner, this))
        {
            return false;
        }

        // An empty transition on the same queue. Enqueuing never runs a body, so this completes only
        // once the start enqueued ahead of it has run.
        await slot
            .EnqueueAsync(this, () => Task.CompletedTask)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (slot.StartFault is { } fault)
        {
            // Captured rather than rethrown, for the reason on AttachHostedServiceAsync.
            ExceptionDispatchInfo.Throw(fault);
        }

        // Read at all because the guards above cannot cover a drain beginning while this wait is
        // queued behind the start: the start body then gates itself out and sets nothing.
        return slot.Current is not null;
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    /// <summary>Tracks a slot this handler installed itself as the owner of, so its drain can stop and release it.</summary>
    internal void TrackForDrain(HostedServiceSlot slot, IInterceptorSubject subject) => _stopOnDrain[slot] = subject;

    /// <summary>
    /// Removes a slot from the drain set. Called by the release, and by an explicit detach, which stops
    /// without releasing ownership.
    /// </summary>
    internal void UntrackForDrain(HostedServiceSlot slot) => _stopOnDrain.TryRemove(slot, out _);

    internal void EnterTransition() => Interlocked.Increment(ref _inFlight);

    internal void LeaveTransition() => Interlocked.Decrement(ref _inFlight);

    /// <summary>How many transitions this handler has enqueued that have not finished. Test only.</summary>
    internal int InFlightTransitionCount => Volatile.Read(ref _inFlight);

    /// <summary>Whether the slot is in the set this handler's drain would stop. Test only.</summary>
    internal bool IsTrackedForDrain(HostedServiceSlot slot) => _stopOnDrain.ContainsKey(slot);

    /// <summary>
    /// Records liveness for a subject already in the graph that hosted nothing when it entered, so
    /// <c>AttachSubject</c> recorded none. The one moment the answer cannot be taken from a slot.
    /// </summary>
    /// <remarks>
    /// The write must stay inside <see cref="LifecycleInterceptor.TryRunWhileAttached"/>'s callback.
    /// Reading membership and then writing releases the lock in between, and a graph move landing in
    /// that gap makes the write land on the opposite answer. The take after it does not need the same
    /// treatment, because it reads liveness under the queue lock and refuses on its own. What asking
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
            LivenessWriteTestHook?.Invoke();
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
        OpenGate();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        if (DrainTestHook is { } drainTestHook)
        {
            await drainTestHook().ConfigureAwait(false);
        }

        // Liveness ends where the drain begins. A handler that still reports a subject as live claims
        // ownership of every attachment added to it afterwards, enqueues a start that no-ops, and never
        // releases it, because the release loop below only covers the drain's own snapshot.
        _liveSubjects.Clear();

        var snapshot = _stopOnDrain.ToArray();

        if (DrainEnqueueTestHook is { } drainEnqueueTestHook)
        {
            await drainEnqueueTestHook().ConfigureAwait(false);
        }

        // The same shape per owned subject as a context detach: a subject's own stop has to return
        // before the attachments it uses are stopped and disposed underneath it. Subject stops first,
        // so each attachment stop finds its subject's signal already chosen rather than creating it.
        foreach (var (slot, subject) in snapshot)
        {
            if (slot.Subject is not null)
            {
                _ = EnqueueStopIfOwned(subject, slot, waitFor: null, cancellationToken);
            }
        }

        foreach (var (slot, subject) in snapshot)
        {
            if (slot.Subject is null)
            {
                // Discarded rather than collected: the count is what the drain waits on, and a stop
                // this enqueue refused is one another handler now owns.
                _ = EnqueueStopIfOwned(subject, slot, SubjectStopToAwait(subject, slot), cancellationToken);
            }
        }

        // Bounded by the token, which for a host is the shutdown deadline. Nothing further down
        // observes it: the queue waits inside a stop body are untokened by design, and a stop wedged
        // behind one of them would otherwise hold the process open forever.
        var remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);

        if (DrainReleaseTestHook is { } drainReleaseTestHook)
        {
            await drainReleaseTestHook().ConfigureAwait(false);
        }

        foreach (var (slot, _) in snapshot)
        {
            // After the stops, so a second host cannot start ahead of this host's stop, and even when
            // the wait above gave up: a wrong stop is recoverable and a slot owned by a dead handler
            // is not.
            slot.ReleaseOwnership(this);
        }

        if (remaining == 0)
        {
            // Read again rather than held: an enqueue landing after the count first reached zero went
            // through the same increment, and only a second read sees it.
            remaining = await WaitForTransitionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (remaining != 0)
        {
            // Logged rather than thrown, so the release above still runs: rethrowing would leave every
            // slot owned by a dead handler and a second host over the same subjects starting nothing.
            _logger?.LogWarning(
                "Shutdown gave up waiting for {Count} hosted service transitions; they keep running unobserved.",
                remaining);
        }

        // The drain set is not cleared: what is left belongs to installs whose own gate re-read releases them.
    }

    /// <summary>
    /// Waits for every transition this handler enqueued to finish, and reports how many were still
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

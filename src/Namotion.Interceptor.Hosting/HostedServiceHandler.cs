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

    private ILogger? _logger;

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    private ILogger? Logger => _logger ??= _loggerResolver();

    /// <summary>
    /// Test seam, awaited in <see cref="StopAsync"/> after the gate begins draining and before the
    /// running set is snapshotted. Null in production. Lets a test hold the drain open so the
    /// "attached during a drain" race does not depend on timing.
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
        _liveSubjects[subject] = 0;

        if (subject is IHostedService hostedService)
        {
            var target = subject.GetOrAddSubjectTarget(hostedService);
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target, CancellationToken.None);
            }
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target, CancellationToken.None);
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

        // Stops are appended NOW, not issued later from inside another transition. Deferring them
        // lets a re-attach's create land first on the attachment chain, after which the deferred stop
        // disposes the NEW instance and leaks the old one.
        if (subjectTarget is not null)
        {
            AppendStop(subject, subjectTarget, subjectStopped, waitFor: null, CancellationToken.None);
        }
        else
        {
            subjectStopped.TrySetResult();
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            AppendStop(subject, target, signal: null, waitFor: subjectStopped.Task, CancellationToken.None);
        }

        // Released after the stops are appended, and never from inside a transition body: releasing
        // from the body would clobber ownership a re-attach has already retaken, and the re-attach's
        // start would then no-op itself.
        subjectTarget?.ReleaseOwnership(this);
        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            ((IHostedServiceAttachmentTarget)attachment).Target.ReleaseOwnership(this);
        }
    }

    internal Task AppendStart(IInterceptorSubject subject, HostedServiceTarget target, CancellationToken cancellationToken)
    {
        _running[target] = subject;

        return target.AppendAsync(async _ =>
        {
            target.SetFault(null);

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

            try
            {
                await Task.Delay(StartDelayMilliseconds, cancellationToken).ConfigureAwait(false);

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
        }, cancellationToken);
    }

    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
    {
        _running.TryRemove(target, out _);

        return target.AppendAsync(async _ =>
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

                await _gate.WaitForOpenAsync().ConfigureAwait(false);
                if (_gate.State == HostedServiceGateState.Drained)
                {
                    return;
                }

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
                catch (Exception exception) when (exception is not OperationCanceledException)
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

    internal async Task WaitForStartAsync(IInterceptorSubject subject, IHostedService hostedService, CancellationToken cancellationToken)
    {
        var target = subject.GetOrAddSubjectTarget(hostedService);
        target.TryTakeOwnership(this);

        await target.AppendAsync(_ => Task.CompletedTask, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (target.Fault is { } fault)
        {
            throw fault;
        }
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

        if (DrainGate is { } drainGate)
        {
            await drainGate().ConfigureAwait(false);
        }

        var snapshot = _running.ToArray();
        var stops = new List<Task>(snapshot.Length);

        foreach (var (target, subject) in snapshot)
        {
            stops.Add(AppendStop(subject, target, signal: null, waitFor: null, cancellationToken));
        }

        await Task.WhenAll(stops).ConfigureAwait(false);

        foreach (var (target, _) in snapshot)
        {
            // Released after the stops so a second host cannot start ahead of this host's stop,
            // and released at all so a second host over the same subjects is not blocked forever.
            target.ReleaseOwnership(this);
        }

        _gate.CompleteDraining();
    }
}

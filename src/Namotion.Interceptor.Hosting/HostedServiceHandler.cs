using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

[RunsAfter(typeof(ContextInheritanceHandler))]
internal class HostedServiceHandler : IHostedService, ILifecycleHandler, IDisposable
{
    private ILogger? _logger;

    private Task? _executeTask;
    private CancellationTokenSource? _stoppingCts;

    private readonly Func<ILogger?> _loggerResolver;
    private readonly BufferBlock<Func<CancellationToken, Task>> _actions = new();
    private readonly HashSet<IHostedService> _hostedServices = [];

    // A second owner for the cleanup a queued start carries in its closure: the pump abandons
    // whatever is still buffered when it exits, and an unreleased hold blocks every wait on the tree
    // forever while an unfinished completion blocks its awaiter just as long.
    private readonly HashSet<PendingStart> _pendingStarts = [];
    private volatile bool _stopped;

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        _logger ??= _loggerResolver();

        if (change.IsContextAttach)
        {
            if (change.Subject is IHostedService hostedService)
            {
                AttachHostedService(hostedService, change.Subject.Context);
            }

            foreach (var hostedService2 in change.Subject.GetAttachedHostedServices())
            {
                AttachHostedService(hostedService2, change.Subject.Context);
            }
        }
        else if (change.IsContextDetach)
        {
            if (change.Subject is IHostedService hostedService)
            {
                DetachHostedService(hostedService);
            }

            foreach (var attachedHostedService in change.Subject.GetAttachedHostedServices())
            {
                // The extension, not this handler's own method: it also clears the subject's
                // attached-services data, which a plain stop would leave behind (pinned by
                // HostedServiceHandlerTests.WhenSubjectServiceIsDetached_ThenHostedServiceIsStopped).
                // It re-resolves the handler through the subject's context, so a subject whose own
                // context has already lost its fallback stops resolving one; that is pre-existing and
                // narrower than losing the data cleanup.
                change.Subject.DetachHostedService(attachedHostedService);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_executeTask is not null)
        {
            return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
        }
        
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = ExecuteAsync(_stoppingCts.Token);
        return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger ??= _loggerResolver();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var action = await _actions.ReceiveAsync(stoppingToken);
                    await action(stoppingToken);
                }
                catch (Exception exception)
                {
                    if (exception is not OperationCanceledException)
                    {
                        _logger?.LogError(exception, "Failed to execute hosted service action.");
                    }
                }
            }
        }
        finally
        {
            _stopped = true;
            ReleaseAbandonedStarts();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null)
        {
            return;
        }

        try
        {
            if (_stoppingCts is not null)
            {
                await _stoppingCts.CancelAsync();
            }
            
            Task[] tasks;
            lock (_hostedServices)
            {
                tasks = _hostedServices
                    .Select(async hostedService =>
                    {
                        try
                        {
                            _logger?.LogInformation("Stopping hosted service {Service}.", hostedService.ToString());
                            await hostedService.StopAsync(cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            if (exception is not OperationCanceledException)
                            {
                                _logger?.LogError(exception, "Failed to stop hosted service {Service}.", hostedService.ToString());
                            }
                        }
                    })
                    .ToArray();
                
                _hostedServices.Clear();
            }
            
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _executeTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    
    internal void AttachHostedService(IHostedService hostedService, IInterceptorSubjectContext context)
    {
        lock (_hostedServices)
        {
            if (_hostedServices.Add(hostedService))
            {
                // Starting is queued, not inline, so the service is NOT running when this returns.
                // Anything that treats "the graph has finished starting" as a completion point would
                // otherwise reach it while this start is still on its way in - concretely, a source
                // attached here would not yet have registered with its SourceMonitor, and a
                // synchronization wait would complete against a tree that is not synchronized.
                // Holds are taken HERE, synchronously, rather than inside the queued action, so
                // there is no window between the attach and the hold in which completion can fire.
                // They are released once the start has actually run (see PostStartService).
                //
                // A nested attach composes: a service that attaches children during its own
                // StartAsync takes their holds before its own is released, so the count never
                // reaches zero in between.
                PostStartService(hostedService, null, TakeStartupHolds(context));
            }
        }
    }

    /// <summary>
    /// Takes a completion hold on every deferrer reachable from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Empty for an application that configures no deferring subsystem (no source monitoring, for
    /// example), which is the common case and costs one empty-array check per attach.
    /// </remarks>
    private static IDisposable[] TakeStartupHolds(IInterceptorSubjectContext context)
    {
        var deferrers = context.GetServices<IStartupCompletionDeferrer>();
        if (deferrers.IsEmpty)
        {
            return [];
        }

        var holds = new IDisposable[deferrers.Length];
        for (var index = 0; index < deferrers.Length; index++)
        {
            holds[index] = deferrers[index].DeferCompletion();
        }

        return holds;
    }

    internal void DetachHostedService(IHostedService hostedService)
    {
        lock (_hostedServices)
        {
            if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, null);
            }
        }
    }

    internal async Task AttachHostedServiceAsync(
        IHostedService hostedService, IInterceptorSubjectContext context, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Add(hostedService))
            {
                // Holds here too, even though this overload's caller awaits the start: the caller
                // being blocked does not block the startup-completion gate, so without a hold
                // ApplicationStarted can fire, drop the count to zero and let a wait complete
                // vacuously while this start is still sitting in the queue.
                PostStartService(hostedService, tcs, TakeStartupHolds(context));
            }
            else
            {
                tcs.TrySetResult(); // Already attached
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }
    
    internal async Task DetachHostedServiceAsync(IHostedService hostedService, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, tcs);
            }
            else
            {
                tcs.TrySetResult(); // Already removed
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }

    private void PostStartService(
        IHostedService hostedService, TaskCompletionSource? tcs, IDisposable[] startupHolds)
    {
        var pending = Track(startupHolds, tcs);

        // Read after the add, which is what makes it decisive: the add happens inside the lock the
        // sweep takes after setting _stopped, so lock release and acquire order that write ahead of
        // this read.
        if (_stopped)
        {
            Abandon(pending);
            return;
        }

        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Starting attached hosted service {Service}.", hostedService.ToString());
                await hostedService.StartAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
            finally
            {
                // In a finally, so a start that throws or is cancelled releases its holds too. The
                // completion is already set by the paths above.
                if (Claim(pending))
                {
                    ReleaseHolds(pending!);
                }
            }
        });
    }

    /// <summary>
    /// Records what a queued start would leave behind if it never ran, or null when there is nothing
    /// to clean up. That is the common case, a fire-and-forget attach in an application with no
    /// deferrer, and it costs one length check and no lock.
    /// </summary>
    private PendingStart? Track(IDisposable[] startupHolds, TaskCompletionSource? completion)
    {
        if (startupHolds.Length == 0 && completion is null)
        {
            return null;
        }

        var pending = new PendingStart(startupHolds, completion);
        lock (_pendingStarts)
        {
            _pendingStarts.Add(pending);
        }

        return pending;
    }

    /// <summary>
    /// Takes ownership of a pending start's cleanup: only the caller that removes it cleans up, so
    /// the sweep and the queued action can never both do it.
    /// </summary>
    private bool Claim(PendingStart? pending)
    {
        if (pending is null)
        {
            return false;
        }

        lock (_pendingStarts)
        {
            return _pendingStarts.Remove(pending);
        }
    }

    private void Abandon(PendingStart? pending)
    {
        if (Claim(pending))
        {
            ReleaseHolds(pending!);
            pending!.Completion?.TrySetCanceled();
        }
    }

    private void ReleaseHolds(PendingStart pending)
    {
        foreach (var hold in pending.StartupHolds)
        {
            try
            {
                hold.Dispose();
            }
            catch (Exception holdException)
            {
                // One deferrer throwing must not strand the others, so the log is guarded too.
                try
                {
                    _logger?.LogError(
                        holdException, "Releasing a startup completion hold threw and was ignored.");
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>Cleans up after every start that was posted but will never run.</summary>
    private void ReleaseAbandonedStarts()
    {
        PendingStart[] abandoned;
        lock (_pendingStarts)
        {
            if (_pendingStarts.Count == 0)
            {
                return;
            }

            // Clearing under the same lock claims all of them at once, so a queued action racing
            // this finds its entry gone and leaves the cleanup here.
            abandoned = [.. _pendingStarts];
            _pendingStarts.Clear();
        }

        foreach (var pending in abandoned)
        {
            ReleaseHolds(pending);
            pending.Completion?.TrySetCanceled();
        }

        _logger?.LogWarning(
            "Cleaned up after {Count} attached service start(s) that will no longer run.",
            abandoned.Length);
    }

    /// <summary>What a queued start leaves behind if the pump never dequeues it.</summary>
    private sealed class PendingStart(IDisposable[] startupHolds, TaskCompletionSource? completion)
    {
        public IDisposable[] StartupHolds { get; } = startupHolds;

        public TaskCompletionSource? Completion { get; } = completion;
    }

    private void PostStopService(IHostedService hostedService, TaskCompletionSource? tcs)
    {
        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Stopping detached hosted service {Service}.", hostedService.ToString());
                await hostedService.StopAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
        });
    }

    public void Dispose()
    {
        // Cancelling is all that is needed: the pump sweeps from its own finally, after the action it
        // is awaiting has finished. Sweeping here would race that action and release the holds of a
        // start still running, opening the completion gate early.
        _stoppingCts?.Cancel();
    }
}

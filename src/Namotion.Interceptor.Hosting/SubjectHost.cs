using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Runs one subject in a context of its own, with property tracking, lifecycle, a hosting handler
/// nothing else shares and whatever <see cref="ISubjectContextConfigurator.ConfigureContext"/> adds.
/// Returned by
/// <see cref="SubjectHostExtensions.StartAsync{TSubject}(TSubject, CancellationToken)"/>. Stopping or
/// disposing it stops and disposes what it started, detaches the subject from the private context and
/// removes every activation the host ran, so the subject and its children are plain data again.
/// </summary>
public sealed class SubjectHost : IAsyncDisposable
{
    private readonly HostedServiceHandler _handler = new(trackActivations: true);
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger? _logger;

    private IInterceptorSubject? _subject;
    private IInterceptorSubjectContext? _context;
    private TaskCompletionSource? _stop;

    internal SubjectHost(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider ?? EmptyServiceProvider.Instance;

        // The activation the handler publishes as the subject attaches carries no provider of its own
        // and creates its service with the handler's, and the explicit activation in StartAsync finds
        // that activation and keeps it, so the handler's provider is the one the service gets.
        _handler.SetServiceProvider(_serviceProvider);
        _logger = _serviceProvider.GetService(typeof(ILogger<HostedServiceHandler>)) as ILogger;
        if (_logger is not null)
        {
            _handler.SetLogger(_logger);
        }
    }

    /// <summary>
    /// Attaches <paramref name="subjectToAttach"/> to the private context when given, else uses the
    /// subject <see cref="Attach"/> attached. Then opens the handler, waits for the subject's own start
    /// when it is a hosted service, activates it and waits for the activated service's start. On any
    /// failure, stops the host before rethrowing, so no half started subject is left behind. The token
    /// bounds that teardown as well, so a cancellation tears down without waiting for a start that is
    /// still running.
    /// </summary>
    internal async Task StartAsync(IInterceptorSubject? subjectToAttach, CancellationToken cancellationToken)
    {
        try
        {
            if (subjectToAttach is not null)
            {
                Attach(subjectToAttach);
            }

            var subject = _subject ?? throw new InvalidOperationException("No subject is attached to the subject host.");
            _handler.EnsureStarted();

            if (subject is IHostedService)
            {
                await _handler.WaitForStartAsync(subject, cancellationToken).ConfigureAwait(false);
            }

            await subject.ActivateHostedServiceAsync(_serviceProvider, _handler, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The caller's token, so a stop that hangs cannot hold a host start past its own deadline,
            // and an already cancelled one makes the drain give up at once rather than wait for a start
            // that has not returned. The stop queued behind that start still stops and disposes the
            // instance once the start returns.
            try
            {
                await StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception teardownException)
            {
                // Reported rather than thrown: the start's exception is the one the caller acts on.
                _logger?.LogError(teardownException, "Stopping the subject host after a failed start threw.");
            }

            throw;
        }
    }

    /// <summary>
    /// Builds the private context and attaches the subject to it. Throws when the subject is already
    /// in a tracked graph, hosting or not: when its context reaches a lifecycle, or when a lifecycle
    /// elsewhere holds a reference to it. A second lifecycle over one subject double counts its
    /// references, and a second hosting handler claims its services.
    /// </summary>
    internal void Attach(IInterceptorSubject subject)
    {
        // The reference count catches a graph whose lifecycle the subject's own context does not reach,
        // because nothing there passed its context down.
        if (subject.GetReferenceCount() > 0 || !subject.Context.GetServices<LifecycleInterceptor>().IsEmpty)
        {
            throw new InvalidOperationException(
                $"Subject {subject} is already in a tracked graph, and a second lifecycle or hosting handler over one subject double counts its references and claims its services. Let a context with WithHostedServices() run it instead, or remove it from that graph before starting it here.");
        }

        // Assigned only past the check, so the teardown of a refused start leaves the other host's
        // subject alone.
        _subject = subject;

        // An activation an earlier host left behind would be reused, with that host's provider. No
        // handler is reachable here, so the detach only clears the subject's data.
        if (subject.TryGetLiveActivation() is { } stale)
        {
            subject.DetachHostedService(stale);
        }

        // Complete before the subject joins: a lifecycle handler added afterwards never sees the
        // attach of the subjects already in the graph.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        (subject as ISubjectContextConfigurator)?.ConfigureContext(context);
        if (context.TryGetService<HostedServiceHandler>() is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(ISubjectContextConfigurator.ConfigureContext)} of {subject} added hosting to the context it runs in alone, which already gets its own.");
        }

        context.AddService(_handler);
        _context = context;
        subject.Context.AddFallbackContext(context);
    }

    /// <summary>
    /// Stops and disposes everything the host started, then detaches the subject from the private
    /// context and removes every activation the host ran. Later calls, and
    /// <see cref="DisposeAsync"/>, return the first call's task and ignore their token.
    /// </summary>
    /// <remarks>
    /// The token bounds the wait for the services to stop. Once it expires the task still completes
    /// successfully, and a service still stopping keeps running unobserved. Awaiting this from a hosted
    /// service's own stop path, or from the unwinding of its <c>ExecuteAsync</c>, waits on itself:
    /// docs/hosting.md#do-not-detach-from-your-own-stop-path.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stop) is { } existing)
        {
            return existing.Task;
        }

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _stop, stop, null) is { } winner)
        {
            return winner.Task;
        }

        // Published before the teardown runs, so a concurrent or re-entrant caller gets this same task
        // rather than starting a second teardown.
        _ = CompleteStopAsync(stop, cancellationToken);
        return stop.Task;
    }

    /// <summary>
    /// Stops without a deadline, so a service whose stop never returns blocks it. Call
    /// <see cref="StopAsync"/> first to bound it, which makes this a no-op.
    /// </summary>
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task CompleteStopAsync(TaskCompletionSource stop, CancellationToken cancellationToken)
    {
        var core = StopCoreAsync(cancellationToken);
        await core.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stop.SetFromTask(core);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The handler is one-shot: its drain is terminal, so a restart builds a new host.
            await _handler.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_context is not null)
                {
                    _subject?.Context.RemoveFallbackContext(_context);
                }
            }
            finally
            {
                DetachActivations();
            }
        }
    }

    /// <summary>
    /// Detaches every activation the host ran, the subject's and those of its children, so a
    /// later context that opted out of activation does not start them.
    /// </summary>
    private void DetachActivations()
    {
        // After the fallback removal, which takes the whole subtree out of the private graph, so each
        // detach only clears data: the drain has already stopped and disposed the instances, or queued
        // the stop that will. Detaching one already gone is a no-op.
        foreach (var (activation, subject) in _handler.TrackedActivations)
        {
            if (IsUnhosted(subject))
            {
                subject.DetachHostedService(activation);
            }
        }
    }

    /// <summary>
    /// Whether no hosting handler is reachable from the subject. A child that moved into another
    /// hosting graph while this host ran keeps its activation and keeps running there after this host
    /// stops, whether or not that graph activates automatically, because that host now runs it.
    /// </summary>
    /// <remarks>
    /// A child moving at the very moment of teardown can be read as unhosted and reach the new host
    /// without a service, the same class of misuse as two concurrent starts of one subject.
    /// </remarks>
    private static bool IsUnhosted(IInterceptorSubject subject)
        => subject.Context.GetServices<HostedServiceHandler>().IsEmpty;
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Forces construction of a DI registered subject at host start. A singleton nobody resolves is never
/// built, never attached to its context and never started, and <see cref="IHostedService"/> is the
/// only hook the generic host offers for forcing that construction.
/// </summary>
internal sealed class SubjectActivation<T> : IHostedService
    where T : class, IInterceptorSubject
{
    private readonly IServiceProvider _serviceProvider;

    private IHostedService? _startedHere;
    private volatile bool _isStopping;

    public SubjectActivation(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving constructs and attaches the subject, which makes the handler append its start.
        var subject = _serviceProvider.GetRequiredService<T>();
        if (subject is not IHostedService hostedService)
        {
            return;
        }

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            // Recorded rather than resolved again in StopAsync: the subject can gain a hosting
            // context between start and stop, and a stop that resolved a handler now would hand the
            // stop to a handler that never started it, leaving it running.
            _startedHere = hostedService;
            await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);

            if (hostedService is BackgroundService { ExecuteTask: { } executeTask })
            {
                // Resolved now: the execution can outlive the provider, which the host disposes after stopping.
                var logger = _serviceProvider.GetService<ILogger<SubjectActivation<T>>>();
                _ = ObserveExecutionAsync(subject, executeTask, logger);
            }

            return;
        }

        // Opens the gate before awaiting, so a handler registered after this activation cannot
        // deadlock host startup on registration order.
        handler.EnsureStarted();

        // A false result is deliberately not a fallback into starting the subject here: another
        // handler owning it would make that a second instance, and a draining handler would make it
        // something nothing stops.
        await handler.WaitForStartAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        return _startedHere?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Logs an execution that ended other than by running to completion, which the generic host does
    /// for the background services it starts and nothing does for one started here. A cancellation
    /// after this activation's own stop is that stop's doing.
    /// </summary>
    /// <remarks>
    /// Logs only. The host's <c>BackgroundServiceExceptionBehavior</c> lives on <c>HostOptions</c> in
    /// Microsoft.Extensions.Hosting, which this library does not reference.
    /// </remarks>
    private async Task ObserveExecutionAsync(T subject, Task executeTask, ILogger? logger)
    {
        try
        {
            await executeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_isStopping && executeTask.IsCanceled)
        {
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Hosted subject {Subject} faulted while running.", subject);
        }
    }
}

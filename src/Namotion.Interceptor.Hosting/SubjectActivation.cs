using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        => _startedHere?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}

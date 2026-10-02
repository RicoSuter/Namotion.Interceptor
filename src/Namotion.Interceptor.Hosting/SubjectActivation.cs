using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Forces construction of a DI registered subject at host start and runs it. A singleton nobody
/// resolves is never built, and <see cref="IHostedService"/> is the only hook the generic host offers
/// for forcing that construction.
/// </summary>
internal sealed class SubjectActivation<T> : IHostedService, IAsyncDisposable, IDisposable
    where T : class, IInterceptorSubject
{
    /// <summary>
    /// The longest disposal waits for the stop of a host that was never stopped: five seconds, after
    /// which disposal returns and a stop still running continues unobserved.
    /// </summary>
    // Bounded, because the generic host disposes without stopping after a failed start, and a stuck
    // stop must not hang the provider's disposal.
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly SubjectRegistration<T> _registration;

    /// <summary>
    /// The private host of this provider's instance, recorded when the instance is created so disposal
    /// still stops a host an awaited attach opened before this activation ever started, or the host
    /// this activation built for a caller registered instance. Assigned before its start, so a stop
    /// after a failed start returns the teardown that start already ran.
    /// </summary>
    private SubjectHost? _host;

    public SubjectActivation(IServiceProvider serviceProvider, SubjectRegistration<T> registration)
    {
        _serviceProvider = serviceProvider;
        _registration = registration;
    }

    internal void RecordHost(SubjectHost host) => _host = host;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subject = _registration.Resolve(_serviceProvider);
        var isCreatedInstance = _registration.TryGetCreatedInstance(subject, out var host);
        if (host is not null)
        {
            await host.StartAsync(subjectToAttach: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null && !isCreatedInstance && _registration.IsSelfContained)
        {
            // The caller registered the instance themselves and it is in no hosting graph, so it gets
            // the private host an instance this registration constructs gets, whatever it hosts.
            var callerInstanceHost = new SubjectHost(_serviceProvider);
            _host = callerInstanceHost;
            await callerInstanceHost.StartAsync(subject, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (subject is not IHostedService)
        {
            return;
        }

        if (handler is null)
        {
            if (isCreatedInstance)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} is registered against a context without hosting, so nothing would run it. " +
                    "Call WithHostedServices() on that context, or register it without a context resolver to run it in a context of its own.");
            }

            throw new InvalidOperationException(
                $"The {typeof(T).Name} instance registered in dependency injection was not constructed by AddSubject, " +
                "so the context resolver was not applied to it, and it is in no hosting graph, so nothing would run it. " +
                "Attach it to a context with WithHostedServices(), or let AddSubject construct it.");
        }

        // Opens the gate before awaiting, so a handler registered after this activation cannot
        // deadlock host startup on registration order.
        handler.EnsureStarted();

        // A false result is deliberately not a fallback into starting the subject here: another
        // handler owning it would make that a second instance.
        await handler.WaitForStartAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _host?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Stops the host this activation holds, a no-op once it has been stopped.</summary>
    /// <remarks>
    /// After a failed host start, the container disposes the subject singleton before disposing this
    /// activation. A subject that is itself disposable (a <see cref="BackgroundService"/>, for instance)
    /// may then be stopped only after its own dispose has already run, and the handler may log a
    /// spurious execution fault for it. Normal shutdown, where the container stops this activation
    /// before disposing the subject, is unaffected.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_host is not { } host)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(DisposeStopTimeout);

        // The token bounds a first stop; the wait bounds a stop already running without a deadline,
        // whose task a later StopAsync returns. Suppressed rather than thrown: when this disposal's own
        // call is the first stop, a fault outside the handler's logging is swallowed because disposal
        // has no caller to report it to.
        await host.StopAsync(timeout.Token)
            .WaitAsync(timeout.Token)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// The synchronous form, for a provider disposed synchronously, which otherwise throws on a service
    /// that is only asynchronously disposable.
    /// </summary>
    /// <remarks>See <see cref="DisposeAsync"/> for the disposal ordering hazard this is subject to.</remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

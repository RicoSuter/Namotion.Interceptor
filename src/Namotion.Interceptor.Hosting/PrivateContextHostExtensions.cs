namespace Namotion.Interceptor.Hosting;

/// <summary>Runs a subject that has a hosted service without a host or dependency injection.</summary>
public static class PrivateContextHostExtensions
{
    /// <summary>
    /// Starts the subject in a private context, waits for its service to start, and returns the
    /// handle that stops it. The service is created with a service provider that resolves nothing.
    /// Throws <see cref="InvalidOperationException"/> when the subject is already started or already in
    /// a tracked graph, and rethrows a start fault after stopping what it started.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">
    /// Bounds the wait for the start. A cancellation tears down with the cancelled token, so services
    /// that did start are stopped without a grace period and disposed, before this throws.
    /// </param>
    public static Task<PrivateContextHost> StartAsync<TSubject>(this TSubject subject, CancellationToken cancellationToken = default)
        where TSubject : IInterceptorSubject, ISubjectHostedServiceFactory
    {
        ArgumentNullException.ThrowIfNull(subject);
        return StartCoreAsync(subject, serviceProvider: null, cancellationToken);
    }

    /// <summary>
    /// Starts the subject in a private context, waits for its service to start, and returns the
    /// handle that stops it. Throws <see cref="InvalidOperationException"/> when the subject is already
    /// started or already in a tracked graph, and rethrows a start fault after stopping what it started.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="serviceProvider">Handed to <see cref="ISubjectHostedServiceFactory.CreateHostedService"/>.</param>
    /// <param name="cancellationToken">
    /// Bounds the wait for the start. A cancellation tears down with the cancelled token, so services
    /// that did start are stopped without a grace period and disposed, before this throws.
    /// </param>
    public static Task<PrivateContextHost> StartAsync<TSubject>(this TSubject subject, IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        where TSubject : IInterceptorSubject, ISubjectHostedServiceFactory
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return StartCoreAsync(subject, serviceProvider, cancellationToken);
    }

    private static async Task<PrivateContextHost> StartCoreAsync(IInterceptorSubject subject, IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        var host = new PrivateContextHost(serviceProvider);
        await host.StartAsync(subject, cancellationToken).ConfigureAwait(false);
        return host;
    }
}

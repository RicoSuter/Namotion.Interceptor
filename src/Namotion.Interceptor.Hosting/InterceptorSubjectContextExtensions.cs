using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Defers hosted-service starts attached in the current execution flow until the scope and its
    /// enclosing scopes are disposed.
    /// Returns null when hosted services are not configured on this context.
    /// </summary>
    public static HostedServiceStartupScope? DeferHostedServiceStartup(this IInterceptorSubjectContext context)
        => context.TryGetService<HostedServiceHandler>()?.DeferStartup();

    /// <summary>
    /// Enables hosted services on the context: a handler registered with the host starts and stops
    /// the services bound to the subjects in the graph. Every <see cref="ISubjectHostedServiceFactory"/>
    /// subject is activated as it attaches to the context.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="serviceCollection">The host's services, which the handler is registered with.</param>
    public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
        => context.WithHostedServices(serviceCollection, activateSubjectHostedServices: true);

    /// <summary>
    /// Enables hosted services on the context: a handler registered with the host starts and stops
    /// the services bound to the subjects in the graph.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="serviceCollection">The host's services, which the handler is registered with.</param>
    /// <param name="activateSubjectHostedServices">
    /// Whether every <see cref="ISubjectHostedServiceFactory"/> subject is activated as it attaches to
    /// the context, with the service provider the host resolves the handler with. When false, such a
    /// subject runs only once activated explicitly with
    /// <see cref="InterceptorHostingExtensions.ActivateHostedService"/>.
    /// </param>
    public static IInterceptorSubjectContext WithHostedServices(
        this IInterceptorSubjectContext context,
        IServiceCollection serviceCollection,
        bool activateSubjectHostedServices)
    {
        context
            .TryAddService(() =>
            {
                var handler = new HostedServiceHandler(activateSubjectHostedServices);

                // A plain Add, not AddHostedService: AddHostedService routes through TryAddEnumerable,
                // which dedupes on the implementation type, so a second context on the same collection
                // would silently lose its handler and never start any of its subjects.
                serviceCollection.AddSingleton<IHostedService>(serviceProvider =>
                {
                    // Deferred to here because the handler is built before any provider exists: the
                    // context creates it, and only the host can resolve a logger for it.
                    handler.SetLogger(serviceProvider.GetRequiredService<ILogger<HostedServiceHandler>>());
                    handler.SetServiceProvider(serviceProvider);
                    return handler;
                });

                return handler;
            }, _ => true);

        return context
            .WithLifecycle();
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Defers hosted-service starts attached in the current execution flow until the returned deferral
    /// and its enclosing deferrals are disposed.
    /// Returns null when hosted services are not configured on this context.
    /// </summary>
    public static HostedServiceStartDeferral? DeferHostedServiceStarts(this IInterceptorSubjectContext context)
        => context.TryGetService<HostedServiceHandler>()?.DeferStarts();

    public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
    {
        context
            .TryAddService(() =>
            {
                ILogger? logger = null;
                var handler = new HostedServiceHandler(() => logger);
                serviceCollection.AddHostedService(sp =>
                {
                    logger = sp.GetRequiredService<ILogger<HostedServiceHandler>>();
                    return handler;
                });
                return handler;
            }, _ => true);

        return context
            .WithLifecycle();
    }
}
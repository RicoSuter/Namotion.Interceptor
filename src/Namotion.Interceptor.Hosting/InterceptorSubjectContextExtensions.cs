using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorSubjectContextExtensions
{
    public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
    {
        // Lifecycle first, so a lifecycle conflict throws before the handler exists anywhere:
        // the factory also registers into the DI service collection, and that side effect must
        // not outlive a failed configuration.
        context.WithLifecycle();

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

        return context;
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorCollectionExtensions
{
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
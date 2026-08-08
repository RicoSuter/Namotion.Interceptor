using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting;

public static class InterceptorSubjectContextExtensions
{
    public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
    {
        context
            .TryAddService(() =>
            {
                ILogger? logger = null;
                var handler = new HostedServiceHandler(() => logger);

                // A plain Add, not AddHostedService: AddHostedService routes through TryAddEnumerable,
                // which dedupes on the implementation type, so a second context on the same collection
                // would silently lose its handler and never start any of its subjects.
                serviceCollection.AddSingleton<IHostedService>(sp =>
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
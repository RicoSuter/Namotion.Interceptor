using System.Runtime.CompilerServices;
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
                // Written and read through a box rather than a captured local, so both ends can carry a
                // fence: the provider resolves the logger on whichever thread first asks it for the
                // hosted services, and every reader is a transition thread with no happens before edge
                // to that one. Without the fence a reader may keep seeing the null it was born with and
                // silently drop the errors this logger exists to report.
                var logger = new StrongBox<ILogger?>(null);
                var handler = new HostedServiceHandler(() => Volatile.Read(ref logger.Value));

                // A plain Add, not AddHostedService: AddHostedService routes through TryAddEnumerable,
                // which dedupes on the implementation type, so a second context on the same collection
                // would silently lose its handler and never start any of its subjects.
                serviceCollection.AddSingleton<IHostedService>(serviceProvider =>
                {
                    Volatile.Write(ref logger.Value, serviceProvider.GetRequiredService<ILogger<HostedServiceHandler>>());
                    return handler;
                });

                return handler;
            }, _ => true);

        return context
            .WithLifecycle();
    }
}
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Completes once the hosted service start and stop actions queued before this call have run,
    /// so services attached through the lifecycle path have actually started.
    /// </summary>
    /// <remarks>
    /// Returns a completed task when no HostedServiceHandler is configured, because nothing was
    /// ever queued.
    /// </remarks>
    public static Task WaitForPendingHostedServiceActionsAsync(
        this IInterceptorSubjectContext context, CancellationToken cancellationToken = default)
    {
        var handler = context.TryGetService<HostedServiceHandler>();
        return handler is null
            ? Task.CompletedTask
            : handler.WaitForPendingActionsAsync(cancellationToken);
    }
}
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Pushover;

public static class PushoverServiceCollectionExtensions
{
    public static IServiceCollection AddPushoverService(
        this IServiceCollection services,
        Action<PushoverService>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

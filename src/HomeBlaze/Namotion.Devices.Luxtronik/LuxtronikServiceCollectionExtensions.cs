using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Luxtronik;

public static class LuxtronikServiceCollectionExtensions
{
    public static IServiceCollection AddLuxtronikHeatPump(
        this IServiceCollection services,
        Action<LuxtronikHeatPump>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

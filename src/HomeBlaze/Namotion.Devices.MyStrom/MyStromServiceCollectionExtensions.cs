using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.MyStrom;

public static class MyStromServiceCollectionExtensions
{
    /// <summary>
    /// Registers a myStrom switch and runs it. Without <paramref name="contextResolver"/> it runs in a
    /// context of its own; with it, it joins the resolved context, which must have hosting.
    /// </summary>
    public static IServiceCollection AddMyStromSwitch(
        this IServiceCollection services,
        Action<MyStromSwitch>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
    {
        services.AddHttpClient();
        return services.AddSubject(configure, contextResolver);
    }

    /// <summary>Registers one of several myStrom switches under a key. See <see cref="AddMyStromSwitch"/>.</summary>
    public static IServiceCollection AddKeyedMyStromSwitch(
        this IServiceCollection services,
        object? serviceKey,
        Action<MyStromSwitch>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
    {
        services.AddHttpClient();
        return services.AddKeyedSubject(serviceKey, configure, contextResolver);
    }
}

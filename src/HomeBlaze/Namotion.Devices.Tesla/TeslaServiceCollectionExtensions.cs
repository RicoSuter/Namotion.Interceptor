using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Tesla;

public static class TeslaServiceCollectionExtensions
{
    /// <summary>Adds a Tesla account as a hosted subject, and the system clock when no <see cref="TimeProvider"/> is registered.</summary>
    public static IServiceCollection AddTeslaAccount(
        this IServiceCollection services,
        Action<TeslaAccount>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddHostedSubject(configure, contextResolver);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Hyundai;

public static class BluelinkServiceCollectionExtensions
{
    /// <summary>Adds a Bluelink account as a hosted subject, and the system clock when no <see cref="TimeProvider"/> is registered.</summary>
    public static IServiceCollection AddBluelinkAccount(
        this IServiceCollection services,
        Action<BluelinkAccount>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services.AddHostedSubject(configure, contextResolver);
    }
}

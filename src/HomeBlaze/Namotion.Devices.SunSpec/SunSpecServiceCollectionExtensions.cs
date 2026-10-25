using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.SunSpec;

/// <summary>
/// Registers a <see cref="SunSpecDevice"/> with dependency injection.
/// </summary>
public static class SunSpecServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="SunSpecDevice"/> as a singleton and hosted service, as
    /// <see cref="HostedSubjectServiceCollectionExtensions.AddHostedSubject{T}"/> does.
    /// </summary>
    public static IServiceCollection AddSunSpecDevice(
        this IServiceCollection services,
        Action<SunSpecDevice>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

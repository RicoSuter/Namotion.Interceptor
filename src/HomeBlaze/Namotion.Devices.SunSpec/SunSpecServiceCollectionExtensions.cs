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
    /// Registers a <see cref="SunSpecDevice"/> and runs it, as
    /// <see cref="SubjectServiceCollectionExtensions.AddSubject{T}"/> does: without
    /// <paramref name="contextResolver"/> in a private context, with it in the resolved context,
    /// which must have hosting and the registry.
    /// </summary>
    public static IServiceCollection AddSunSpecDevice(
        this IServiceCollection services,
        Action<SunSpecDevice>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}

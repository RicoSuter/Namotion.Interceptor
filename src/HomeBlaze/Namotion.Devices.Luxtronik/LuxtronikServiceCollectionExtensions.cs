using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Registers a <see cref="LuxtronikHeatPump"/> with dependency injection.
/// </summary>
public static class LuxtronikServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="LuxtronikHeatPump"/> as a singleton attached to the context at host start, as
    /// <see cref="SubjectServiceCollectionExtensions.AddSubject{T}"/> does.
    /// </summary>
    public static IServiceCollection AddLuxtronikHeatPump(
        this IServiceCollection services,
        Action<LuxtronikHeatPump>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}

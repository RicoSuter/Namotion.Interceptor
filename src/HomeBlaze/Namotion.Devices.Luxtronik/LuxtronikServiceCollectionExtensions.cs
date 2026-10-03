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
    /// Registers a <see cref="LuxtronikHeatPump"/> and runs it, as
    /// <see cref="SubjectServiceCollectionExtensions.AddSubject{T}"/> does: without
    /// <paramref name="contextResolver"/> in a context of its own, with it in the resolved context,
    /// which must have hosting and the registry.
    /// </summary>
    public static IServiceCollection AddLuxtronikHeatPump(
        this IServiceCollection services,
        Action<LuxtronikHeatPump>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}

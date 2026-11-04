using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Nuki;

/// <summary>
/// Registers a <see cref="NukiBridge"/> with dependency injection.
/// </summary>
public static class NukiServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="NukiBridge"/> as a singleton and hosted service, as
    /// <see cref="HostedSubjectServiceCollectionExtensions.AddHostedSubject{T}"/> does.
    /// </summary>
    public static IServiceCollection AddNukiBridge(
        this IServiceCollection services,
        Action<NukiBridge>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

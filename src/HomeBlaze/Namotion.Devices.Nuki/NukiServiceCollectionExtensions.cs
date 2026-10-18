using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Nuki;

public static class NukiServiceCollectionExtensions
{
    /// <summary>
    /// Adds a Nuki bridge as hosted subject.
    /// </summary>
    public static IServiceCollection AddNukiBridge(
        this IServiceCollection services,
        Action<NukiBridge>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

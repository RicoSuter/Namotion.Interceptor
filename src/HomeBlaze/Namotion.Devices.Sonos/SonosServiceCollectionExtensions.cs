using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Sonos;

public static class SonosServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="SonosSystem"/> as a hosted subject.
    /// </summary>
    public static IServiceCollection AddSonosSystem(
        this IServiceCollection services,
        Action<SonosSystem>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

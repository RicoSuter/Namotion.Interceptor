using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Extension methods for registering a <see cref="FritzBox"/> with dependency injection.
/// </summary>
public static class FritzBoxServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="FritzBox"/> as a hosted subject, for a plain .NET host without HomeBlaze.
    /// </summary>
    public static IServiceCollection AddFritzBox(
        this IServiceCollection services,
        Action<FritzBox>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

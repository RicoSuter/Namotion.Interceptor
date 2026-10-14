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
    /// Adds a <see cref="FritzBox"/> as a hosted subject, for a plain .NET host without HomeBlaze, as
    /// <see cref="HostedSubjectServiceCollectionExtensions.AddHostedSubject{T}"/> does.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the instance after creation.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the <see cref="IInterceptorSubjectContext"/>; see
    /// <see cref="HostedSubjectServiceCollectionExtensions.AddHostedSubject{T}"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFritzBox(
        this IServiceCollection services,
        Action<FritzBox>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}

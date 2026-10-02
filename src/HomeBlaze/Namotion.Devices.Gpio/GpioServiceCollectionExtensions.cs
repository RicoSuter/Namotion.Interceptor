using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Extension methods for registering GPIO services with dependency injection.
/// </summary>
public static class GpioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GPIO subject and runs it, as <see cref="SubjectServiceCollectionExtensions.AddSubject{T}"/> does.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the GPIO subject.</param>
    /// <param name="contextResolver">
    /// Optional resolver for a shared context the subject joins, which must have hosting. Without it,
    /// the subject runs in a context of its own.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddGpio(
        this IServiceCollection services,
        Action<GpioSubject>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);
}

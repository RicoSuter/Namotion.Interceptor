using Namotion.Interceptor;
using Namotion.Proxy.AspNetCore.Controllers;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ProxyAspNetCoreServiceCollection
{
    /// <summary>
    /// Registers a generic controller with the signature 'ProxyController{TProxy} : ProxyControllerBase{TProxy} where TProxy : class'.
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    /// <typeparam name="TSubject">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddProxyControllers<TSubject, TController>(this IServiceCollection services)
        where TController : ProxyControllerBase<TSubject>
        where TSubject : class, IInterceptorSubject
    {
        services
            .AddControllers()
            .ConfigureApplicationPartManager(setup =>
            {
                setup.FeatureProviders.Add(new ProxyControllerFeatureProvider<TController>());
            });

        return services;
    }
}


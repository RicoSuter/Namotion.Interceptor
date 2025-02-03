using Namotion.Interceptor;
using Namotion.Proxy.AspNetCore.Controllers;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectAspNetCoreServiceCollection
{
    /// <summary>
    /// Registers a generic controller with the signature 'SubjectController{TProxy} : SubjectControllerBase{TProxy} where TProxy : class'.
    /// </summary>
    /// <typeparam name="TController">The controller type.</typeparam>
    /// <typeparam name="TSubject">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddSubjectController<TSubject, TController>(this IServiceCollection services)
        where TController : SubjectControllerBase<TSubject>
        where TSubject : class, IInterceptorSubject
    {
        services
            .AddControllers()
            .ConfigureApplicationPartManager(setup =>
            {
                setup.FeatureProviders.Add(new SubjectControllerFeatureProvider<TController, TSubject>());
            });

        return services;
    }
}


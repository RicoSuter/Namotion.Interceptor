using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;

namespace HomeBlaze.Services;

/// <summary>
/// Factory for creating a fully configured InterceptorSubjectContext
/// with all standard interceptors enabled.
/// </summary>
public static class SubjectContextFactory
{
    /// <summary>
    /// Creates an InterceptorSubjectContext with full tracking, registry,
    /// validation, and hosted service support.
    /// </summary>
    /// <param name="services">The service collection for hosted services registration.</param>
    /// <param name="serviceProvider">Optional service provider to register on context for DI access from interceptors.</param>
    public static IInterceptorSubjectContext Create(IServiceCollection services, IServiceProvider? serviceProvider = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithReadPropertyRecorder()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer)
            .WithDataAnnotationValidation()
            .WithHostedServices(services);
        
        // Register service provider on context for interceptors that need DI access
        if (serviceProvider != null)
        {
            // TODO: Maybe instead of this we should register a background service which adds the service provider to the context in ctor?
            context.AddService(serviceProvider);
        }

        return context;
    }
}

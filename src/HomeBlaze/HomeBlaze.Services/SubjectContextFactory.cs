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
    public static IInterceptorSubjectContext Create(IServiceCollection services)
    {
        return InterceptorSubjectContext
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
    }
}

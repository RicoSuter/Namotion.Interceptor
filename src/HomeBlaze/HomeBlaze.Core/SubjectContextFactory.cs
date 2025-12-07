using HomeBlaze.Core.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace HomeBlaze.Core;

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
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithPathResolver()
            .WithDataAnnotationValidation()
            .WithHostedServices(services);
    }
}

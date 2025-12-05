using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace HomeBlaze.Core.Services;

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
            .WithDataAnnotationValidation()
            .WithHostedServices(services);
    }

    /// <summary>
    /// Creates an InterceptorSubjectContext for testing (no hosted services).
    /// </summary>
    public static IInterceptorSubjectContext CreateForTesting()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithDataAnnotationValidation();
    }
}

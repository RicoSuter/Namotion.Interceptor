using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// A service provider wrapper that excludes IInterceptorSubjectContext from resolution.
/// This ensures subjects are created without context injection, allowing context to be
/// attached later via ContextInheritanceHandler when the subject is assigned to a property.
/// This guarantees that configuration properties are fully populated before the subject
/// enters the lifecycle system.
/// </summary>
public class SubjectCreationServiceProvider : IServiceProvider, IServiceProviderIsService
{
    private readonly IServiceProvider _inner;

    public SubjectCreationServiceProvider(IServiceProvider inner)
    {
        _inner = inner;
    }

    public object? GetService(Type serviceType)
    {
        // Return ourselves for IServiceProviderIsService so ActivatorUtilities
        // uses our IsService implementation for constructor selection
        if (serviceType == typeof(IServiceProviderIsService))
        {
            return this;
        }

        // Don't inject context - let ContextInheritanceHandler handle it later
        // This ensures properties are deserialized before hosted services start
        if (serviceType == typeof(IInterceptorSubjectContext))
        {
            return null;
        }

        return _inner.GetService(serviceType);
    }

    /// <summary>
    /// Tells ActivatorUtilities whether a service is available.
    /// By returning false for IInterceptorSubjectContext, ActivatorUtilities
    /// will choose a constructor that doesn't require it.
    /// </summary>
    public bool IsService(Type serviceType)
    {
        if (serviceType == typeof(IInterceptorSubjectContext))
        {
            return false;
        }

        // Delegate to inner provider if it implements IServiceProviderIsService
        if (_inner is IServiceProviderIsService isService)
        {
            return isService.IsService(serviceType);
        }

        // Fallback: assume service is available if we can get it
        return _inner.GetService(serviceType) != null;
    }
}

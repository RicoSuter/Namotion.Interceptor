using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services;

/// <summary>
/// Factory for creating subject instances using dependency injection.
/// </summary>
public class SubjectFactory
{
    private readonly IServiceProvider _serviceProvider;

    public SubjectFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates a new instance of the specified subject type. Its hosted service is not activated, see
    /// <see cref="ActivateHostedService"/>.
    /// </summary>
    public IInterceptorSubject CreateSubject(Type type)
    {
        var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
        if (instance is IInterceptorSubject subject)
        {
            return subject;
        }

        throw new InvalidOperationException(
            $"Type {type.FullName} must implement IInterceptorSubject.");
    }

    /// <summary>
    /// Creates a new instance of the specified subject type.
    /// </summary>
    public T CreateSubject<T>() where T : IInterceptorSubject
    {
        return (T)CreateSubject(typeof(T));
    }

    /// <summary>
    /// Activates the hosted service of a created subject with the application's service provider.
    /// </summary>
    /// <remarks>
    /// Call once the subject is configured and only when it is kept: a subject created with the
    /// application's context is already attached to it, so the service starts right away and keeps
    /// running until the subject is detached.
    /// </remarks>
    public IHostedServiceAttachment? ActivateHostedService(IInterceptorSubject subject)
    {
        return subject.ActivateHostedService(_serviceProvider);
    }
}

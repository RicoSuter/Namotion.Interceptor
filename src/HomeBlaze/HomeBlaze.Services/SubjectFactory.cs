using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

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
    /// Creates a new instance of the specified subject type.
    /// </summary>
    /// <remarks>
    /// A type with a generated context constructor is attached to the root context right away: it is known to the
    /// registry and, when it is a hosted service, starts. Pass a subject that is not handed over to a parent to
    /// <see cref="DiscardSubject"/>.
    /// </remarks>
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
    /// Detaches a subject created by this factory that was never handed over to a parent: it leaves the registry
    /// and, when it is a hosted service, is stopped. Does nothing for a subject that was created detached.
    /// </summary>
    public void DiscardSubject(IInterceptorSubject subject)
    {
        if (_serviceProvider.GetService<IInterceptorSubjectContext>() is { } rootContext)
        {
            subject.Context.RemoveFallbackContext(rootContext);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

// TODO: Add unit tests for SubjectFactory - CreateSubject with valid/invalid types, dependency injection scenarios

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
}

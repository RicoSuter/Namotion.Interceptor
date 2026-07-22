using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// The central context for managing services and intercepting property/method access on interceptor subjects.
/// Provides service registration, retrieval, and execution of intercepted operations through middleware chains.
/// </summary>
public interface IInterceptorSubjectContext
{
    /// <summary>
    /// Registers a service instance directly with the context.
    /// </summary>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="service">The service instance to add.</param>
    void AddService<TService>(TService service);

    /// <summary>
    /// Conditionally registers a service using a factory function.
    /// The factory is only invoked if the <paramref name="exists"/> predicate returns false for all existing services of this type.
    /// </summary>
    /// <typeparam name="TService">The type of service to register.</typeparam>
    /// <param name="factory">Factory function to create the service instance.</param>
    /// <param name="exists">Predicate to check against existing services. If any existing service matches (returns true), the factory is not invoked.</param>
    /// <returns>True if the service was added, false if a matching service already exists.</returns>
    bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists);

    /// <summary>
    /// Retrieves a service of the specified type, or null if not registered.
    /// </summary>
    /// <typeparam name="TInterface">The type of service to retrieve.</typeparam>
    /// <returns>The service instance, or null if not found.</returns>
    TInterface? TryGetService<TInterface>();

    /// <summary>
    /// Retrieves all registered services of the specified type.
    /// </summary>
    /// <typeparam name="TInterface">The type of services to retrieve.</typeparam>
    /// <returns>An immutable array of all matching services.</returns>
    ImmutableArray<TInterface> GetServices<TInterface>();

    /// <summary>
    /// Adds a fallback context for service resolution.
    /// Services not found in this context will be looked up in fallback contexts.
    /// </summary>
    /// <param name="context">The fallback context to add.</param>
    /// <returns>True if the fallback context was added, false if it was already present.</returns>
    bool AddFallbackContext(IInterceptorSubjectContext context);

    /// <summary>
    /// Removes a previously added fallback context.
    /// </summary>
    /// <param name="context">The fallback context to remove.</param>
    /// <returns>True if the fallback context was removed, false if it was not present.</returns>
    bool RemoveFallbackContext(IInterceptorSubjectContext context);
}
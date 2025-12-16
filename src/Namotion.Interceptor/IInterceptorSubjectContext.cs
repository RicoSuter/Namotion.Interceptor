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
    /// Executes a property read operation through the interceptor chain.
    /// </summary>
    /// <typeparam name="TProperty">The type of the property being read.</typeparam>
    /// <param name="context">The property read context containing metadata about the operation.</param>
    /// <param name="readValue">The delegate to read the actual property value.</param>
    /// <returns>The property value, potentially modified by interceptors.</returns>
    TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue);

    /// <summary>
    /// Executes a property write operation through the interceptor chain.
    /// </summary>
    /// <typeparam name="TProperty">The type of the property being written.</typeparam>
    /// <param name="context">The property write context containing metadata and the new value.</param>
    /// <param name="writeValue">The delegate to write the actual property value.</param>
    void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue);

    /// <summary>
    /// Executes a method invocation through the interceptor chain.
    /// </summary>
    /// <param name="context">The method invocation context containing metadata about the operation.</param>
    /// <param name="invokeMethod">The delegate to invoke the actual method.</param>
    /// <returns>The method return value, potentially modified by interceptors.</returns>
    object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod);

    // TODO: Remove Execute* methods here (not needed in the interface)
    
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
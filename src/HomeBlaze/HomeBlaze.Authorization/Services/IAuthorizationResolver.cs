using HomeBlaze.Abstractions.Authorization;
using Namotion.Interceptor;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Resolves authorization requirements for subjects, properties, and methods.
/// Implements a priority chain: property override → subject override → attribute → parent → defaults.
/// </summary>
public interface IAuthorizationResolver
{
    /// <summary>
    /// Resolves the roles required to access a property.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <param name="entity">The entity type (State or Configuration).</param>
    /// <param name="action">The action (Read or Write).</param>
    /// <returns>Array of roles that grant access (any role is sufficient).</returns>
    string[] ResolvePropertyRoles(PropertyReference property, AuthorizationEntity entity, AuthorizationAction action);

    /// <summary>
    /// Resolves the roles required to access a subject at the subject level.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="entity">The entity type.</param>
    /// <param name="action">The action.</param>
    /// <returns>Array of roles that grant access.</returns>
    string[] ResolveSubjectRoles(IInterceptorSubject subject, AuthorizationEntity entity, AuthorizationAction action);

    /// <summary>
    /// Resolves the roles required to invoke a method.
    /// </summary>
    /// <param name="subject">The subject containing the method.</param>
    /// <param name="entity">The entity type (Query or Operation).</param>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>Array of roles that grant access.</returns>
    string[] ResolveMethodRoles(IInterceptorSubject subject, AuthorizationEntity entity, string methodName);

    /// <summary>
    /// Invalidates the cache for a specific subject (call when extension data changes).
    /// </summary>
    /// <param name="subject">The subject whose cache should be invalidated.</param>
    void InvalidateCache(IInterceptorSubject subject);
}

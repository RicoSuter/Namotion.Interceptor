using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Authorization.Interceptors;

/// <summary>
/// Interceptor that enforces authorization on property read/write operations.
/// Uses [RunsFirst] semantics to ensure auth check happens before other interceptors.
/// </summary>
public class AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor
{
    // Cache for property entity type lookups
    private readonly ConcurrentDictionary<(Type, string), AuthorizationEntity> _entityCache = new();

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        // Get authorization resolver from DI
        var resolver = GetAuthorizationResolver(context.Property.Subject);
        if (resolver == null)
        {
            // Graceful degradation if services not available
            return next(ref context);
        }

        // Determine entity type from property marker attribute
        var entity = GetPropertyEntity(context.Property);

        // Resolve required roles
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, entity, AuthorizationAction.Read);

        // Check authorization - for reads, return default instead of throwing
        // This allows UI to gracefully degrade when user lacks permissions
        if (!AuthorizationContext.HasAnyRole(requiredRoles))
        {
            return default!;
        }

        return next(ref context);
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Get authorization resolver from DI
        var resolver = GetAuthorizationResolver(context.Property.Subject);
        if (resolver == null)
        {
            // Graceful degradation if services not available
            next(ref context);
            return;
        }

        // Determine entity type from property marker attribute
        var entity = GetPropertyEntity(context.Property);

        // Resolve required roles
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, entity, AuthorizationAction.Write);

        // Check authorization
        if (!AuthorizationContext.HasAnyRole(requiredRoles))
        {
            var userName = AuthorizationContext.CurrentUser?.Identity?.Name ?? "anonymous";
            throw new UnauthorizedAccessException(
                $"Access denied: User '{userName}' cannot write {context.Property.Name}. " +
                $"Required roles: [{string.Join(", ", requiredRoles)}]");
        }

        next(ref context);
    }

    private IAuthorizationResolver? GetAuthorizationResolver(IInterceptorSubject subject)
    {
        var serviceProvider = subject.Context.TryGetService<IServiceProvider>();
        return serviceProvider?.GetService<IAuthorizationResolver>();
    }

    private AuthorizationEntity GetPropertyEntity(PropertyReference property)
    {
        var type = property.Subject.GetType();
        var key = (type, property.Name);

        return _entityCache.GetOrAdd(key, k =>
        {
            var propertyInfo = k.Item1.GetProperty(k.Item2);
            if (propertyInfo == null)
            {
                return AuthorizationEntity.State; // Default
            }

            // Check for [Configuration] attribute
            if (propertyInfo.GetCustomAttribute<ConfigurationAttribute>() != null)
            {
                return AuthorizationEntity.Configuration;
            }

            // Default to State (includes [State] attribute or no attribute)
            return AuthorizationEntity.State;
        });
    }
}

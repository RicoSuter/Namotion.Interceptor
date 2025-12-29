using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Authorization.Roles;

namespace HomeBlaze.Authorization.Configuration;

/// <summary>
/// Configuration options for the HomeBlaze authorization system.
/// </summary>
public class AuthorizationOptions
{
    /// <summary>
    /// The role assigned to unauthenticated users.
    /// Default: Anonymous
    /// </summary>
    public string UnauthenticatedRole { get; set; } = DefaultRoles.Anonymous;

    /// <summary>
    /// Default required roles for each AuthorizationEntity+Action combination.
    /// Used when no specific authorization is defined on a subject or property.
    /// </summary>
    public Dictionary<(AuthorizationEntity Entity, AuthorizationAction Action), string[]> DefaultPermissionRoles { get; set; } = new()
    {
        [(AuthorizationEntity.State, AuthorizationAction.Read)] = [DefaultRoles.Anonymous],
        [(AuthorizationEntity.State, AuthorizationAction.Write)] = [DefaultRoles.Operator],
        [(AuthorizationEntity.Configuration, AuthorizationAction.Read)] = [DefaultRoles.User],
        [(AuthorizationEntity.Configuration, AuthorizationAction.Write)] = [DefaultRoles.Supervisor],
        [(AuthorizationEntity.Query, AuthorizationAction.Invoke)] = [DefaultRoles.User],
        [(AuthorizationEntity.Operation, AuthorizationAction.Invoke)] = [DefaultRoles.Operator]
    };

    /// <summary>
    /// Gets the default required roles for a given entity and action.
    /// </summary>
    public string[] GetDefaultRoles(AuthorizationEntity entity, AuthorizationAction action)
    {
        return DefaultPermissionRoles.TryGetValue((entity, action), out var roles)
            ? roles
            : [DefaultRoles.Admin]; // Fallback to Admin if not configured
    }
}

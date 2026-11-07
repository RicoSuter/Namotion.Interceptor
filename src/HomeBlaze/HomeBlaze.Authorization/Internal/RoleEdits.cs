using HomeBlaze.Services;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Adds, renames and deletes custom roles for the operations of <see cref="Authorization"/> and <see cref="Role"/>: each
/// checks and changes <see cref="Authorization.Roles"/> under <see cref="AuthorizationEditLock"/> in one update of the
/// <see cref="AuthorizationService"/>, then writes the configuration as the save of an edit dialog does.
/// </summary>
internal static class RoleEdits
{
    public const string DetachedMessage = "The role is not part of an Authorization subject. It may have been deleted, or its file reloaded.";

    public const string DetachedAuthorizationMessage = "The Authorization subject is not part of the graph. It may have been deleted.";

    public static async Task AddAsync(
        Authorization authorization, string name, AuthorizationService authorizationService, RootManager rootManager)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(rootManager);

        var roleName = name.Trim();
        lock (AuthorizationEditLock.Instance)
        {
            if (authorization.TryGetRegisteredSubject() is not { IsAttached: true })
            {
                throw new InvalidOperationException(DetachedAuthorizationMessage);
            }

            var roles = authorization.Roles;
            ThrowIfInvalid(roleName, roles, except: null);
            var changed = CopyRoles(roles, except: null);
            changed.Add(roleName, new Role());
            using (authorizationService.BeginUpdate())
            {
                authorization.Roles = changed;
            }
        }

        await PersistAsync(authorization, rootManager, $"The role '{roleName}' was added");
    }

    public static async Task RenameAsync(Role role, string name, AuthorizationService authorizationService, RootManager rootManager)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(rootManager);

        var newName = name.Trim();
        Authorization authorization;
        lock (AuthorizationEditLock.Instance)
        {
            (authorization, var oldName, var roles) = GetHolder(role);
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return;
            }

            ThrowIfInvalid(newName, roles, except: role);

            var kept = CopyRoles(roles, except: role);
            var renamed = new Dictionary<string, Role>(kept, StringComparer.OrdinalIgnoreCase) { [newName] = role };
            using (authorizationService.BeginUpdate())
            {
                // The registry keeps the key a role was attached under while the role stays in the dictionary, which
                // would leave this role with its old path and name. Left out once, it is attached again under its new key.
                authorization.Roles = kept;
                authorization.Roles = renamed;
            }
        }

        await PersistAsync(authorization, rootManager, $"The role was renamed to '{newName}'");
    }

    public static async Task DeleteAsync(Role role, AuthorizationService authorizationService, RootManager rootManager)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(rootManager);

        Authorization authorization;
        string name;
        lock (AuthorizationEditLock.Instance)
        {
            (authorization, name, var roles) = GetHolder(role);
            var remaining = CopyRoles(roles, except: role);
            using (authorizationService.BeginUpdate())
            {
                authorization.Roles = remaining;
            }
        }

        await PersistAsync(authorization, rootManager, $"The role '{name}' was deleted");
    }

    // Names compare ignoring case, as roles resolve; the renamed role may keep its own name in another case.
    private static void ThrowIfInvalid(string name, Dictionary<string, Role>? roles, Role? except)
    {
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Enter a name.");
        }

        if (AuthorizationService.GetRoleNameError(name) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        foreach (var (key, role) in roles ?? [])
        {
            if (!ReferenceEquals(role, except) && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A role with this name exists already.");
            }
        }
    }

    // A new dictionary, as only a replaced one is noticed. A file written by hand can hold two roles whose names differ
    // only in case, which this dictionary cannot, so that throws instead of dropping one of them.
    private static Dictionary<string, Role> CopyRoles(Dictionary<string, Role>? roles, Role? except)
    {
        var copy = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, role) in roles ?? [])
        {
            if (!ReferenceEquals(role, except) && !copy.TryAdd(key, role))
            {
                throw new InvalidOperationException($"Several roles are named '{key}', ignoring case. Rename or delete one of them first.");
            }
        }

        return copy;
    }

    // Read under the edit lock: a delete or a reload of the file takes the role out of its Authorization, and a change
    // made through the one it had before would bring it back or be lost.
    private static (Authorization Authorization, string Name, Dictionary<string, Role> Roles) GetHolder(Role role)
    {
        var (authorization, name) = role.FindParent();
        if (authorization is null || name is null)
        {
            throw new InvalidOperationException(DetachedMessage);
        }

        var roles = authorization.Roles;
        if (roles is null || !roles.TryGetValue(name, out var heldRole) || !ReferenceEquals(heldRole, role))
        {
            throw new InvalidOperationException(DetachedMessage);
        }

        return (authorization, name, roles);
    }

    // As an edit dialog persists its save: written through the configuration writer of the Authorization, then applied,
    // also when nothing was written. The owner is the Authorization, as a deleted role has no parents to find a writer by.
    private static async Task PersistAsync(Authorization authorization, RootManager rootManager, string change)
    {
        bool isWritten;
        try
        {
            isWritten = await rootManager.GetConfigurationWriter(authorization).WriteConfigurationAsync(authorization, CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{change}, but the configuration could not be saved ({exception.Message}) and will be lost on restart.", exception);
        }

        await authorization.ApplyConfigurationAsync(CancellationToken.None);
        if (!isWritten)
        {
            throw new InvalidOperationException($"{change}, but the configuration was not saved and will be lost on restart.");
        }
    }
}

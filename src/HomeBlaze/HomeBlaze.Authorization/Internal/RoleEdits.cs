using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Adds, edits, renames and deletes custom roles for the operations of <see cref="Authorization"/> and <see cref="Role"/>
/// through the <see cref="IRoleStore"/>, which validates each change, commits it and only then shows it in
/// <see cref="Authorization.Roles"/>. A refusal is thrown as an <see cref="InvalidOperationException"/> with its reason.
/// </summary>
internal static class RoleEdits
{
    public const string DetachedMessage = "The role is not part of an Authorization subject. It may have been deleted.";

    public const string DetachedAuthorizationMessage = "The Authorization subject is not part of the graph. It may have been deleted.";

    public static async Task AddAsync(Authorization authorization, string name, IRoleStore roleStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(roleStore);

        if (authorization.TryGetRegisteredSubject() is not { IsAttached: true })
        {
            throw new InvalidOperationException(DetachedAuthorizationMessage);
        }

        var roleName = GetName(name);
        var result = await roleStore.SaveRoleAsync(null, new AuthorizationRole { Name = roleName }, null, cancellationToken);
        ThrowIfRefusedOrIncomplete(result);
    }

    public static async Task RenameAsync(Role role, string name, IRoleStore roleStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(roleStore);

        var oldName = GetHeldName(role);
        var newName = GetName(name);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var result = await roleStore.RenameRoleAsync(oldName, newName, cancellationToken);
        ThrowIfRefusedOrIncomplete(result);
    }

    public static async Task EditAsync(
        Role role,
        IReadOnlyList<string> includes,
        IReadOnlyList<PermissionRule> allow,
        IReadOnlyList<PermissionRule> deny,
        IRoleStore roleStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(allow);
        ArgumentNullException.ThrowIfNull(deny);
        ArgumentNullException.ThrowIfNull(roleStore);

        var (name, stamp) = GetHeldNameAndStamp(role, roleStore);
        var editedRole = new AuthorizationRole { Name = name, Includes = [.. includes], Allow = [.. allow], Deny = [.. deny] };
        var result = await roleStore.SaveRoleAsync(name, editedRole, stamp, cancellationToken);
        ThrowIfRefusedOrIncomplete(result);
    }

    public static async Task DeleteAsync(Role role, IRoleStore roleStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleStore);

        var (name, stamp) = GetHeldNameAndStamp(role, roleStore);
        var result = await roleStore.DeleteRoleAsync(name, stamp, cancellationToken);
        ThrowIfRefusedOrIncomplete(result);
    }

    private static string GetName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed : throw new InvalidOperationException("Enter a name.");
    }

    // The role store replaces every role on each change, so a role no Authorization holds was replaced or deleted since.
    private static string GetHeldName(Role role)
    {
        if (role.IsBuiltIn)
        {
            throw new InvalidOperationException(Role.BuiltInRoleReason);
        }

        var (authorization, name) = role.FindParent();
        if (authorization is null || name is null ||
            !authorization.Roles.TryGetValue(name, out var heldRole) || !ReferenceEquals(heldRole, role))
        {
            throw new InvalidOperationException(DetachedMessage);
        }

        return name;
    }

    // The stamp of the rules the role subject holds, which a change replaces with a new subject.
    private static (string Name, string? Stamp) GetHeldNameAndStamp(Role role, IRoleStore roleStore)
    {
        var name = GetHeldName(role);
        var stamp = roleStore.GetConcurrencyStamp(name);

        // A role replaced before the stamp was read has newer rules than the ones shown, which the stamp would overwrite.
        GetHeldName(role);
        return (name, stamp);
    }

    // A committed change with a warning is reported as failed too, so that the person reads what was not completed.
    private static void ThrowIfRefusedOrIncomplete(RoleChangeResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Error ?? "The change was refused.");
        }

        if (result.Warning is { } warning)
        {
            throw new InvalidOperationException(warning);
        }
    }
}

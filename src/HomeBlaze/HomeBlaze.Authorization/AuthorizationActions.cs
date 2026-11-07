using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The actions a HomeBlaze rule may name.
/// </summary>
public static class AuthorizationActions
{
    /// <summary>Running a query or an operation.</summary>
    public static readonly PermissionAction Execute = new("execute");

    /// <summary>Reading the recorded history of a property.</summary>
    public static readonly PermissionAction History = new("history");

    /// <summary>
    /// Administering: managing users, and the administration a full administrator does. Agents and MCP clients are
    /// always denied it.
    /// </summary>
    public static readonly PermissionAction Administer = new("administer");

    /// <summary>Every action a rule may name, the known actions to compile a policy with.</summary>
    public static readonly ImmutableArray<PermissionAction> All =
        [PermissionAction.Read, PermissionAction.Write, Execute, History, Administer];

    /// <summary>
    /// Gets whether <paramref name="action"/> is one a member is ever asked for: read and administer on every member,
    /// execute on methods, and write and history on the other members.
    /// </summary>
    public static bool AppliesTo(PermissionAction action, bool isMethod) =>
        action == PermissionAction.Read || action == Administer || (isMethod ? action == Execute : action != Execute);
}

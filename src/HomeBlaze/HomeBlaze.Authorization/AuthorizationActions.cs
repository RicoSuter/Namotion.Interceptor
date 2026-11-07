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

    /// <summary>Every action a rule may name, the known actions to compile a policy with.</summary>
    public static readonly ImmutableArray<PermissionAction> All = [PermissionAction.Read, PermissionAction.Write, Execute, History];
}

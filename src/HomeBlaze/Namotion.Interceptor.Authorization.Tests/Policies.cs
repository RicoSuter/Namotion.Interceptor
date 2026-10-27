using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

internal static class Policies
{
    public static readonly PermissionAction Execute = new("execute");
    public static readonly PermissionAction History = new("history");
    public static readonly PermissionAction[] KnownActions = [PermissionAction.Read, PermissionAction.Write, Execute, History];

    /// <summary>The provider every policy test compiles with, so the tests also cover a separator other than the default.</summary>
    public static readonly DefaultPathProvider SlashPaths = new('/');

    /// <summary>Compiles a policy holding all the given roles directly.</summary>
    public static AuthorizationPolicy Compile(params AuthorizationRole[] roles)
        => Compile(roles, roles.Select(role => role.Name).ToArray(), systemDeny: []);

    public static AuthorizationPolicy Compile(
        AuthorizationRole[] roles, string[] roleNames, ImmutableArray<PermissionRule> systemDeny, IMemberTagProvider? tags = null)
        => AuthorizationPolicy.Compile(roles, roleNames, systemDeny, KnownActions, tags ?? TestTags.Default, Policies.SlashPaths);

    public static PermissionRule Rule(
        PermissionAction[] actions, string[]? paths = null, string[]? types = null, string[]? members = null, string[]? tags = null)
        => new()
        {
            Actions = [.. actions],
            Paths = paths is null ? [] : [.. paths],
            Types = types is null ? [] : [.. types],
            Members = members is null ? [] : [.. members],
            Tags = tags is null ? [] : [.. tags]
        };

    public static AuthorizationRole Role(string name, PermissionRule[]? allow = null, PermissionRule[]? deny = null, string[]? includes = null)
        => new()
        {
            Name = name,
            Allow = allow is null ? [] : [.. allow],
            Deny = deny is null ? [] : [.. deny],
            Includes = includes is null ? [] : [.. includes]
        };
}

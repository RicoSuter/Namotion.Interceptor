using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The roles, system deny rules and known actions of an application, validated and compiled once, from which
/// <see cref="Compile"/> builds the policy of each principal. A broken role fails only the principals that hold or
/// include it. Immutable and safe to share across threads.
/// </summary>
public sealed class AuthorizationDefinition
{
    private readonly FrozenDictionary<string, AuthorizationRole> _rolesByName;
    private readonly FrozenDictionary<string, CompiledRole> _compiledRoles;
    private readonly CompiledRule[] _systemDeny;
    private readonly FrozenDictionary<string, ulong> _actionBits;
    private readonly IMemberTagProvider _tagProvider;
    private readonly PathProviderBase _pathProvider;

    private AuthorizationDefinition(
        FrozenDictionary<string, AuthorizationRole> rolesByName,
        FrozenDictionary<string, CompiledRole> compiledRoles,
        CompiledRule[] systemDeny,
        FrozenDictionary<string, ulong> actionBits,
        IMemberTagProvider tagProvider,
        PathProviderBase pathProvider)
    {
        _rolesByName = rolesByName;
        _compiledRoles = compiledRoles;
        _systemDeny = systemDeny;
        _actionBits = actionBits;
        _tagProvider = tagProvider;
        _pathProvider = pathProvider;
    }

    /// <summary>
    /// Validates the role names, the known actions and the system deny rules, and compiles the rules of every role. A
    /// role whose rules are invalid does not fail the definition; <see cref="Compile"/> throws its error for every
    /// principal holding or including it.
    /// </summary>
    /// <param name="roles">All roles, by which principals' role names and includes are resolved.</param>
    /// <param name="systemDenyRules">Deny rules of the application that no role can override.</param>
    /// <param name="knownActions">Every action a rule may name; <see cref="PermissionAction.Read"/> and <see cref="PermissionAction.Write"/> are always known. At most 64.</param>
    /// <param name="pathProvider">Defines path syntax and builds the paths that patterns are matched against.</param>
    /// <param name="tagProvider">Supplies the tags of members; required when a rule selects tags.</param>
    /// <exception cref="AuthorizationException">
    /// A role is null, has no name, has a name with leading or trailing whitespace or a control or format character, is
    /// defined more than once or uses <see cref="AuthorizationPolicy.SystemRoleName"/>.
    /// A system deny rule is invalid, as listed for rules under <see cref="Compile"/>.
    /// A known action has no name, or there are more than 64 known actions.
    /// </exception>
    public static AuthorizationDefinition Create(
        IReadOnlyCollection<AuthorizationRole> roles,
        IReadOnlyCollection<PermissionRule> systemDenyRules,
        IReadOnlyCollection<PermissionAction> knownActions,
        PathProviderBase pathProvider,
        IMemberTagProvider? tagProvider = null)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(systemDenyRules);
        ArgumentNullException.ThrowIfNull(knownActions);
        ArgumentNullException.ThrowIfNull(pathProvider);

        var rolesByName = RoleClosure.Index(roles);
        var actionBits = PolicyCompiler.BuildActionBits(knownActions);

        var hasTagProvider = tagProvider is not null;
        var system = new AuthorizationRole
        {
            Name = AuthorizationPolicy.SystemRoleName,
            Deny = systemDenyRules is ImmutableArray<PermissionRule> { IsDefault: true } ? [] : [.. systemDenyRules]
        };
        var systemDeny = PolicyCompiler.CompileRules(system, system.Deny, actionBits, pathProvider, hasTagProvider);

        var compiledRoles = new Dictionary<string, CompiledRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            compiledRoles[role.Name] = CompileRole(role, actionBits, pathProvider, hasTagProvider);
        }

        return new AuthorizationDefinition(
            rolesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            compiledRoles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            systemDeny,
            actionBits,
            tagProvider ?? EmptyTagProvider.Instance,
            pathProvider);
    }

    /// <summary>Builds the policy of a principal holding <paramref name="roleNames"/>, with the roles they include and the system deny rules.</summary>
    /// <param name="roleNames">The roles of the principal.</param>
    /// <exception cref="AuthorizationException">
    /// A role name or include name of the principal's roles and the roles they include is empty, has leading or trailing
    /// whitespace or a control or format character, or names no role of the definition; includes form a cycle, or are
    /// nested more than 64 levels deep.
    /// A rule of those roles is null, has no actions, names an unknown action, has a member, type or tag value that is
    /// empty, has leading or trailing whitespace or contains a control or format character, or selects tags while no tag
    /// provider is given.
    /// A path pattern of those rules starts with '/' or '\' that is not the provider's separator, is malformed, has an
    /// empty segment (a doubled or trailing separator), has a segment with leading or trailing whitespace, contains a
    /// control or format character, or matches no member because it is the separator alone or its last segment has an index.
    /// </exception>
    public AuthorizationPolicy Compile(IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        var closure = RoleClosure.Resolve(_rolesByName, roleNames);
        var allow = new List<CompiledRule>();
        var deny = new List<CompiledRule>();
        foreach (var role in closure)
        {
            var compiled = _compiledRoles[role.Name];
            if (compiled.Error is not null)
            {
                throw new AuthorizationException(compiled.Error);
            }

            allow.AddRange(compiled.Allow);
            deny.AddRange(compiled.Deny);
        }

        deny.AddRange(_systemDeny);
        return new AuthorizationPolicy([.. closure], [.. allow], [.. deny], _actionBits, _tagProvider, _pathProvider);
    }

    private static CompiledRole CompileRole(
        AuthorizationRole role, FrozenDictionary<string, ulong> actionBits, PathProviderBase pathProvider, bool hasTagProvider)
    {
        try
        {
            return new CompiledRole(
                PolicyCompiler.CompileRules(role, role.Allow, actionBits, pathProvider, hasTagProvider),
                PolicyCompiler.CompileRules(role, role.Deny, actionBits, pathProvider, hasTagProvider),
                Error: null);
        }
        catch (AuthorizationException exception)
        {
            return new CompiledRole([], [], exception.Message);
        }
    }

    // The error is kept as a message and thrown as a new exception each time, so concurrent compilations never share one instance.
    private sealed record CompiledRole(CompiledRule[] Allow, CompiledRule[] Deny, string? Error);
}

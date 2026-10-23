using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Turns roles into compiled rules: the include closure, one bit per known action, parsed selectors.</summary>
internal static class PolicyCompiler
{
    public const int MaxActions = 64;

    public sealed record Result(
        ImmutableArray<AuthorizationRole> Roles,
        CompiledRule[] Allow,
        CompiledRule[] Deny,
        FrozenDictionary<string, ulong> ActionBits);

    public static Result Compile(
        IReadOnlyCollection<AuthorizationRole> roles,
        IReadOnlyCollection<string> roleNames,
        ImmutableArray<PermissionRule> systemDenyRules,
        IReadOnlyCollection<PermissionAction> knownActions,
        PathProviderBase pathProvider)
    {
        foreach (var role in roles)
        {
            if (role is not null && string.Equals(role.Name, AuthorizationPolicy.SystemRoleName, StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthorizationException($"Role name '{AuthorizationPolicy.SystemRoleName}' is reserved.");
            }
        }

        var closure = RoleClosure.Resolve(roles, roleNames);
        var actionBits = BuildActionBits(knownActions);

        var allow = new List<CompiledRule>();
        var deny = new List<CompiledRule>();
        foreach (var role in closure)
        {
            AddRules(role, role.Allow, allow, actionBits, pathProvider);
            AddRules(role, role.Deny, deny, actionBits, pathProvider);
        }

        var system = new AuthorizationRole { Name = AuthorizationPolicy.SystemRoleName, Deny = systemDenyRules.IsDefault ? [] : systemDenyRules };
        AddRules(system, system.Deny, deny, actionBits, pathProvider);

        return new Result([.. closure], [.. allow], [.. deny], actionBits);
    }

    private static FrozenDictionary<string, ulong> BuildActionBits(IReadOnlyCollection<PermissionAction> knownActions)
    {
        var bits = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [PermissionAction.Read.Name] = 1UL,
            [PermissionAction.Write.Name] = 1UL << 1
        };

        var next = 2;
        foreach (var action in knownActions)
        {
            if (string.IsNullOrWhiteSpace(action.Name))
            {
                throw new AuthorizationException("A known action has no name.");
            }

            if (bits.ContainsKey(action.Name))
            {
                continue;
            }

            if (next >= MaxActions)
            {
                throw new AuthorizationException($"At most {MaxActions} actions are supported.");
            }

            bits[action.Name] = 1UL << next++;
        }

        return bits.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddRules(
        AuthorizationRole role,
        ImmutableArray<PermissionRule> rules,
        List<CompiledRule> target,
        FrozenDictionary<string, ulong> actionBits,
        PathProviderBase pathProvider)
    {
        if (rules.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new AuthorizationException($"A rule of role '{role.Name}' is null.");
            }

            target.Add(CompileRule(role, rule, actionBits, pathProvider));
        }
    }

    private static CompiledRule CompileRule(
        AuthorizationRole role, PermissionRule rule, FrozenDictionary<string, ulong> actionBits, PathProviderBase pathProvider)
    {
        if (rule.Actions.IsDefaultOrEmpty)
        {
            throw new AuthorizationException($"A rule of role '{role.Name}' has no actions.");
        }

        var actions = 0UL;
        foreach (var action in rule.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Name) || !actionBits.TryGetValue(action.Name, out var bit))
            {
                throw new AuthorizationException($"Action '{action.Name}' of role '{role.Name}' is not a known action.");
            }

            actions |= bit;
        }

        var paths = Normalize(rule.Paths, role, "path");
        return new CompiledRule
        {
            Role = role,
            Rule = rule,
            Actions = actions,
            Paths = paths is null ? null : Array.ConvertAll(paths, path => PathPattern.Parse(path, pathProvider, role.Name)),
            Types = Normalize(rule.Types, role, "type"),
            Members = Normalize(rule.Members, role, "member"),
            Tags = Normalize(rule.Tags, role, "tag")
        };
    }

    private static string[]? Normalize(ImmutableArray<string> values, AuthorizationRole role, string kind)
    {
        if (values.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AuthorizationException($"A rule of role '{role.Name}' has an empty {kind}.");
            }
        }

        return [.. values];
    }
}

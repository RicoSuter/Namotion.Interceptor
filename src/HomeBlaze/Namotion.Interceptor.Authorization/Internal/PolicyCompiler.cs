using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Turns rules into compiled rules: one bit per known action, parsed selectors.</summary>
internal static class PolicyCompiler
{
    public const int MaxActions = 64;

    public const ulong ReadBit = 1UL;

    public const ulong WriteBit = 1UL << 1;

    /// <summary>Creates a policy without rules or known actions, which answers no to every action.</summary>
    public static AuthorizationPolicy CreateDenyAll()
        => new([], [], [], FrozenDictionary<string, ulong>.Empty, EmptyTagProvider.Instance, DefaultPathProvider.Instance);

    public static FrozenDictionary<string, ulong> BuildActionBits(IReadOnlyCollection<PermissionAction> knownActions)
    {
        var bits = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [PermissionAction.Read.Name] = ReadBit,
            [PermissionAction.Write.Name] = WriteBit
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

    /// <summary>Names the owner of a rule in an error: the role, or the system rules for a null role name.</summary>
    public static string DescribeOwner(string? roleName) => roleName is null ? "the system rules" : $"role '{roleName}'";

    /// <summary>Compiles the rules of a role, or the system deny rules for a null role, in their order; <paramref name="hasTagProvider"/> false rejects a rule with tags, which no member would carry.</summary>
    public static CompiledRule[] CompileRules(
        AuthorizationRole? role,
        ImmutableArray<PermissionRule> rules,
        FrozenDictionary<string, ulong> actionBits,
        PathProviderBase pathProvider,
        bool hasTagProvider)
    {
        if (rules.IsDefaultOrEmpty)
        {
            return [];
        }

        var compiled = new CompiledRule[rules.Length];
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            if (rule is null)
            {
                throw new AuthorizationException($"A rule of {DescribeOwner(role?.Name)} is null.");
            }

            compiled[index] = CompileRule(role, rule, actionBits, pathProvider, hasTagProvider);
        }

        return compiled;
    }

    private static CompiledRule CompileRule(
        AuthorizationRole? role, PermissionRule rule, FrozenDictionary<string, ulong> actionBits, PathProviderBase pathProvider, bool hasTagProvider)
    {
        var roleName = role?.Name;
        if (rule.Actions.IsDefaultOrEmpty)
        {
            throw new AuthorizationException($"A rule of {DescribeOwner(roleName)} has no actions.");
        }

        var actions = 0UL;
        foreach (var action in rule.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Name) || !actionBits.TryGetValue(action.Name, out var bit))
            {
                throw new AuthorizationException($"Action '{action.Name}' of {DescribeOwner(roleName)} is not a known action.");
            }

            actions |= bit;
        }

        var paths = Normalize(rule.Paths, roleName, "path");
        var compiled = new CompiledRule
        {
            Role = role,
            Rule = rule,
            Actions = actions,
            Paths = paths is null ? null : Array.ConvertAll(paths, path => PathPattern.Parse(path, pathProvider, roleName)),
            Types = Normalize(rule.Types, roleName, "type"),
            Members = Normalize(rule.Members, roleName, "member"),
            Tags = Normalize(rule.Tags, roleName, "tag")
        };

        if (compiled.Tags is not null && !hasTagProvider)
        {
            // Without a provider no member carries a tag, so a deny with tags would silently never apply.
            throw new AuthorizationException($"A rule of {DescribeOwner(roleName)} selects tags, but no tag provider is given.");
        }

        return compiled;
    }

    private static string[]? Normalize(ImmutableArray<string> values, string? roleName, string kind)
    {
        if (values.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AuthorizationException($"A rule of {DescribeOwner(roleName)} has an empty {kind}.");
            }

            if (SelectorText.IsInvalid(value))
            {
                throw new AuthorizationException(
                    $"A rule of {DescribeOwner(roleName)} has the {kind} '{value}' with leading or trailing whitespace or an invisible character.");
            }
        }

        return [.. values];
    }
}

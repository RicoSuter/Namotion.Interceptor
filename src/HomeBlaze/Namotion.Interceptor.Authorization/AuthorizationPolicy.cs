using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The compiled permissions of one principal. An action on a member is allowed when any allow rule of the
/// principal's roles matches it and no deny rule of the roles or of the system rules does. Immutable and safe to
/// share across threads.
/// </summary>
public sealed class AuthorizationPolicy
{
    /// <summary>The name of the role that <see cref="Explain(RegisteredSubjectProperty, PermissionAction)"/> reports for a system deny rule; no role may use it.</summary>
    public const string SystemRoleName = "System";

    // Along a single-parent chain the walk allocates nothing; a branch or this depth starts the visited set, so a subject is explored once and a cycle ends.
    private const int VisitedTrackingDepth = 32;

    private const int MaxPaths = 64;

    // The root's single empty path, which a member of the root follows; never mutated, so it is shared.
    private static readonly List<PathLink[]> RootPaths = [[]];

    private static readonly TypeNameCache TypeNames = new();

    private readonly CompiledRule[] _allow;
    private readonly CompiledRule[] _deny;
    private readonly FrozenDictionary<string, ulong> _actionBits;
    private readonly IMemberTagProvider? _tagProvider;
    private readonly PathProviderBase? _pathProvider;

    private AuthorizationPolicy(
        ImmutableArray<AuthorizationRole> roles,
        CompiledRule[] allow,
        CompiledRule[] deny,
        FrozenDictionary<string, ulong> actionBits,
        IMemberTagProvider? tagProvider,
        PathProviderBase? pathProvider)
    {
        Roles = roles;
        _allow = allow;
        _deny = deny;
        _actionBits = actionBits;
        _tagProvider = tagProvider;
        _pathProvider = pathProvider;
    }

    /// <summary>A policy that denies every action; the policy of a principal whose roles failed to compile.</summary>
    public static AuthorizationPolicy DenyAll { get; } = new([], [], [], FrozenDictionary<string, ulong>.Empty, null, null);

    /// <summary>Gets the roles of the principal and every role they include, included roles first.</summary>
    public ImmutableArray<AuthorizationRole> Roles { get; }

    /// <summary>
    /// Compiles the policy of a principal holding <paramref name="roleNames"/>.
    /// </summary>
    /// <param name="roles">All defined roles, by which names are resolved.</param>
    /// <param name="roleNames">The roles of the principal.</param>
    /// <param name="systemDenyRules">Deny rules of the application that no role can override.</param>
    /// <param name="knownActions">Every action a rule may name; <see cref="PermissionAction.Read"/> and <see cref="PermissionAction.Write"/> are always known. At most 64.</param>
    /// <param name="tagProvider">Supplies the tags of members.</param>
    /// <param name="pathProvider">Defines path syntax and writes the paths that patterns are matched against.</param>
    /// <exception cref="AuthorizationException">
    /// A role is null, has no name, is defined more than once or uses <see cref="SystemRoleName"/>.
    /// A role or include name is unknown or empty, includes form a cycle, or includes are nested more than 64 levels deep.
    /// A rule is null, has no actions, names an unknown action or has an empty selector value.
    /// A path pattern is malformed, does not start with the provider's separator, has a segment with leading or trailing whitespace,
    /// or matches no member because it is the separator alone or its last segment has an index.
    /// A known action has no name, or there are more than 64 known actions.
    /// </exception>
    public static AuthorizationPolicy Compile(
        IReadOnlyCollection<AuthorizationRole> roles,
        IReadOnlyCollection<string> roleNames,
        ImmutableArray<PermissionRule> systemDenyRules,
        IReadOnlyCollection<PermissionAction> knownActions,
        IMemberTagProvider tagProvider,
        PathProviderBase pathProvider)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(roleNames);
        ArgumentNullException.ThrowIfNull(knownActions);
        ArgumentNullException.ThrowIfNull(tagProvider);
        ArgumentNullException.ThrowIfNull(pathProvider);

        var compiled = PolicyCompiler.Compile(roles, roleNames, systemDenyRules, knownActions, pathProvider);
        return new AuthorizationPolicy(compiled.Roles, compiled.Allow, compiled.Deny, compiled.ActionBits, tagProvider, pathProvider);
    }

    /// <summary>Whether the action is allowed on the member.</summary>
    /// <exception cref="ArgumentException">The action is not a known action of this policy; <see cref="DenyAll"/> answers no to any action instead.</exception>
    public bool IsAllowed(RegisteredSubjectProperty member, PermissionAction action)
    {
        ArgumentNullException.ThrowIfNull(member);
        return IsAllowed(member, GetBit(action));
    }

    /// <summary>Whether the member may be read.</summary>
    public bool CanRead(RegisteredSubjectProperty member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return IsAllowed(member, PolicyCompiler.ReadBit);
    }

    /// <summary>Whether at least one member of the subject is readable and the subject is reachable from the root through readable subject-holding properties.</summary>
    public bool CanRead(RegisteredSubject subject) => Explain(subject).IsReadable;

    /// <summary>Whether the member may be written.</summary>
    public bool CanWrite(RegisteredSubjectProperty member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return IsAllowed(member, PolicyCompiler.WriteBit);
    }

    /// <summary>Answers <see cref="IsAllowed(RegisteredSubjectProperty, PermissionAction)"/> and names the rules that decided it. May allocate; not for hot paths.</summary>
    /// <exception cref="ArgumentException">The action is not a known action of this policy; <see cref="DenyAll"/> answers no to any action instead.</exception>
    public PermissionDecision Explain(RegisteredSubjectProperty member, PermissionAction action)
    {
        ArgumentNullException.ThrowIfNull(member);
        var bit = GetBit(action);
        MatchContext context = default;
        context.SetMember(member);
        var allowed = FindMatch(_allow, bit, isDeny: false, ref context);
        var denied = FindMatch(_deny, bit, isDeny: true, ref context);
        return new PermissionDecision(allowed is not null && denied is null, allowed?.Role, allowed?.Rule, denied?.Role, denied?.Rule);
    }

    /// <summary>Answers <see cref="CanRead(RegisteredSubject)"/> and, when the subject is hidden, says why.</summary>
    public SubjectReadDecision Explain(RegisteredSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!HasReadableMember(subject))
        {
            return new SubjectReadDecision(IsReadable: false, HasReadableMember: false, DeniedProperty: null);
        }

        HashSet<RegisteredSubject>? visited = null;
        RegisteredSubjectProperty? deniedProperty = null;
        var isReachable = IsReachable(subject, 0, ref visited, ref deniedProperty);
        return new SubjectReadDecision(isReachable, HasReadableMember: true, isReachable ? null : deniedProperty);
    }

    private ulong GetBit(PermissionAction action)
    {
        if (_actionBits.TryGetValue(action.Name ?? string.Empty, out var bit))
        {
            return bit;
        }

        if (_actionBits.Count == 0)
        {
            // DenyAll knows no actions and answers no to all of them.
            return 0;
        }

        throw new ArgumentException($"Action '{action}' is not a known action of this policy.", nameof(action));
    }

    private bool IsAllowed(RegisteredSubjectProperty member, ulong bit)
    {
        MatchContext context = default;
        context.SetMember(member);
        return IsAllowed(bit, ref context);
    }

    private bool IsAllowed(ulong bit, ref MatchContext context)
        => FindMatch(_allow, bit, isDeny: false, ref context) is not null && FindMatch(_deny, bit, isDeny: true, ref context) is null;

    private CompiledRule? FindMatch(CompiledRule[] rules, ulong bit, bool isDeny, ref MatchContext context)
    {
        foreach (var rule in rules)
        {
            if (Matches(rule, bit, isDeny, ref context))
            {
                return rule;
            }
        }

        return null;
    }

    // Selectors are checked cheapest first; the paths are enumerated once per evaluation and only when a rule with paths gets that far.
    private bool Matches(CompiledRule rule, ulong bit, bool isDeny, ref MatchContext context)
        => (rule.Actions & bit) != 0 && MatchesMemberSelectors(rule, ref context) && MatchesPaths(rule.Paths, isDeny, ref context);

    private bool MatchesMemberSelectors(CompiledRule rule, ref MatchContext context)
    {
        if (rule.Members is not null && !Glob.IsMatchAny(rule.Members, context.MemberName ??= GetName(context.Member)))
        {
            return false;
        }

        if (rule.Tags is not null && !HasAllTags(context.Tags ??= _tagProvider!.GetTags(context.Member), rule.Tags))
        {
            return false;
        }

        if (rule.Types is not null && !TypeNames.IsAssignableToAny(context.Member.Parent.Subject.GetType(), rule.Types))
        {
            return false;
        }

        return true;
    }

    // A deny rule fails closed: when some path of the subject could not be written, it may be the one the rule names.
    private bool MatchesPaths(PathPattern[]? patterns, bool isDeny, ref MatchContext context)
    {
        if (patterns is null)
        {
            return true;
        }

        context.Paths ??= GetPaths(context.Member.Parent, out context.PathsComplete);
        if (isDeny && !context.PathsComplete)
        {
            return true;
        }

        context.MemberSegment ??= SubjectPaths.GetSegment(_pathProvider!, context.Member);
        return AnyPathMatches(patterns, context.Paths, new PathLink(context.MemberSegment, null, null));
    }

    /// <summary>The name a member glob matches: the property name, or <c>Property@Attribute</c> for an attribute, nested for an attribute of an attribute.</summary>
    private static string GetName(RegisteredSubjectProperty member)
        => member.IsAttribute ? GetName(member.GetAttributedProperty()) + "@" + member.BrowseName : member.Name;

    private static bool HasAllTags(IReadOnlyCollection<string> memberTags, string[] requiredTags)
    {
        foreach (var required in requiredTags)
        {
            if (!ContainsTag(memberTags, required))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsTag(IReadOnlyCollection<string> memberTags, string tag)
    {
        // An array avoids the enumerator allocation of the interface, which is why providers are asked to return one.
        if (memberTags is string[] array)
        {
            foreach (var memberTag in array)
            {
                if (string.Equals(memberTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var memberTag in memberTags)
        {
            if (string.Equals(memberTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Every path of the subject; the root has the single empty path. Incomplete when a path could not be written, more
    // than MaxPaths exist, or the subject has parents but no path was found.
    private List<PathLink[]> GetPaths(RegisteredSubject subject, out bool isComplete)
    {
        if (subject.Parents.Length == 0)
        {
            isComplete = true;
            return RootPaths;
        }

        var paths = new List<PathLink[]>(1);
        isComplete = SubjectPaths.TryGetPaths(subject, _pathProvider!, paths, MaxPaths) && paths.Count > 0;
        return paths;
    }

    private static bool AnyPathMatches(PathPattern[] patterns, List<PathLink[]> paths, in PathLink member)
    {
        foreach (var pattern in patterns)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (pattern.Matches(paths[i], member))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasReadableMember(RegisteredSubject subject)
    {
        MatchContext context = default;
        foreach (var member in subject.Properties)
        {
            context.SetMember(member);
            if (IsAllowed(PolicyCompiler.ReadBit, ref context))
            {
                return true;
            }
        }

        return false;
    }

    // True when some chain of readable subject-holding properties leads to a root. Otherwise deniedProperty is the first
    // unreadable link found, or stays null when every chain returns to a subject already walked.
    private bool IsReachable(RegisteredSubject subject, int depth, ref HashSet<RegisteredSubject>? visited, ref RegisteredSubjectProperty? deniedProperty)
    {
        var parents = subject.Parents;
        if (parents.Length == 0)
        {
            return true;
        }

        // Already explored from here, or a cycle: no new route to a root through this subject.
        if ((parents.Length > 1 || depth >= VisitedTrackingDepth) && !(visited ??= []).Add(subject))
        {
            return false;
        }

        foreach (var parent in parents)
        {
            if (!IsAllowed(parent.Property, PolicyCompiler.ReadBit))
            {
                deniedProperty ??= parent.Property;
            }
            else if (IsReachable(parent.Property.Parent, depth + 1, ref visited, ref deniedProperty))
            {
                return true;
            }
        }

        return false;
    }

    // The facts one question derives, each at most once and only when a rule needs it.
    private struct MatchContext
    {
        public RegisteredSubjectProperty Member;
        public string? MemberName;
        public IReadOnlyCollection<string>? Tags;
        public string? MemberSegment;
        public List<PathLink[]>? Paths;
        public bool PathsComplete;

        // The paths belong to the subject, not the member, so they are kept for the next member of the same subject.
        public void SetMember(RegisteredSubjectProperty member)
        {
            Member = member;
            MemberName = null;
            Tags = null;
            MemberSegment = null;
        }
    }
}

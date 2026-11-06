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

    private const int MaxConsistencyAttempts = 3;

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
    /// <param name="pathProvider">Defines path syntax and builds the paths that patterns are matched against.</param>
    /// <exception cref="AuthorizationException">
    /// A role is null, has no name, is defined more than once or uses <see cref="SystemRoleName"/>.
    /// A role or include name is unknown or empty, has leading or trailing whitespace or a control or format character, includes form a cycle, or includes are nested more than 64 levels deep.
    /// A rule is null, has no actions, names an unknown action, or has a member, type or tag value that is empty, has leading or trailing whitespace or contains a control or format character.
    /// A path pattern starts with '/' or '\' that is not the provider's separator, is malformed, has an empty segment
    /// (a doubled or trailing separator), has a segment with leading or trailing whitespace, contains a control or format character, or matches no member
    /// because it is the separator alone or its last segment has an index.
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
        var context = new MatchContext();
        context.SetMember(member);
        var allowed = FindMatch(_allow, bit, isDeny: false, ref context);
        var denied = FindMatch(_deny, bit, isDeny: true, ref context);
        context.ReturnRentedState();
        return new PermissionDecision(allowed is not null && denied is null, allowed?.Role, allowed?.Rule, denied?.Role, denied?.Rule);
    }

    /// <summary>Answers <see cref="CanRead(RegisteredSubject)"/> and, when the subject is hidden, says why.</summary>
    public SubjectReadDecision Explain(RegisteredSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // The member checks, the walk and its link checks record into one instance, so the answer rests on one instant.
        var state = EvaluationState.Rent();
        try
        {
            for (var attempt = 0; attempt < MaxConsistencyAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    state.Clear();
                }

                var decision = ExplainOnce(subject, state);
                if (state.Reads.AreUnchanged())
                {
                    return decision;
                }
            }

            return new SubjectReadDecision(IsReadable: false, HasReadableMember: false, DeniedProperty: null);
        }
        finally
        {
            EvaluationState.Return(state);
        }
    }

    private SubjectReadDecision ExplainOnce(RegisteredSubject subject, EvaluationState state)
    {
        if (!HasReadableMember(subject, state, out var subjectPaths))
        {
            return new SubjectReadDecision(IsReadable: false, HasReadableMember: false, DeniedProperty: null);
        }

        RegisteredSubjectProperty? deniedProperty = null;
        var isReachable = IsReachable(subject, state, subjectPaths, ref deniedProperty);
        return new SubjectReadDecision(isReachable, HasReadableMember: true, isReachable ? null : deniedProperty);
    }

    private ulong GetBit(PermissionAction action)
    {
        if (_actionBits.TryGetValue(action.Name, out var bit))
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
        var context = new MatchContext();
        context.SetMember(member);
        var allowed = IsAllowed(bit, ref context);
        context.ReturnRentedState();
        return allowed;
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
        => (rule.Actions & bit) != 0 && MatchesMemberSelectors(rule, isDeny, ref context) && MatchesPaths(rule.Paths, isDeny, ref context);

    private bool MatchesMemberSelectors(CompiledRule rule, bool isDeny, ref MatchContext context)
    {
        if (rule.Members is not null)
        {
            if (!context.IsMemberNameResolved)
            {
                context.MemberName = GetName(context.Member);
                context.IsMemberNameResolved = true;
            }

            // An unknown name fails closed like incomplete paths: a deny applies to it, an allow does not.
            if (context.MemberName is null ? !isDeny : !Glob.IsMatchAny(rule.Members, context.MemberName))
            {
                return false;
            }
        }

        if (rule.Tags is not null && !HasAllTags(GetTags(ref context), rule.Tags))
        {
            return false;
        }

        if (rule.Types is not null && !TypeNames.IsAssignableToAny(context.Member.Parent.Subject.GetType(), rule.Types))
        {
            return false;
        }

        return true;
    }

    // A deny rule fails closed: when some path of the subject could not be written, it may be the one the rule names,
    // and it matches the structural spellings of each link whether or not a property shadows them.
    private bool MatchesPaths(PathPattern[]? patterns, bool isDeny, ref MatchContext context)
    {
        if (patterns is null)
        {
            return true;
        }

        if (!context.HasPaths)
        {
            context.Paths = GetPaths(context.Member.Parent, ref context);
            context.HasPaths = true;
        }

        if (isDeny && !context.Paths.IsComplete)
        {
            return true;
        }

        context.MemberSegment ??= SubjectPaths.GetSegment(_pathProvider!, context.Member);
        return context.Paths.AnyMatches(patterns, context.MemberSegment, isDeny);
    }

    private ImmutableArray<string> GetTags(ref MatchContext context)
    {
        if (context.Tags.IsDefault)
        {
            var tags = _tagProvider!.GetTags(context.Member);
            if (tags.IsDefault)
            {
                throw new InvalidOperationException(
                    $"{_tagProvider.GetType().FullName}.GetTags returned a default array; return an empty array for a member without tags.");
            }

            context.Tags = tags;
        }

        return context.Tags;
    }

    /// <summary>
    /// The name a member glob matches: the property name, or <c>Property@Attribute</c> for an attribute, nested for an
    /// attribute of an attribute. Null when the attribute's chain of properties is broken: a property is missing or the
    /// attributes name each other in a cycle, as the registry also tolerates.
    /// </summary>
    private static string? GetName(RegisteredSubjectProperty member)
    {
        if (!member.IsAttribute)
        {
            return member.Name;
        }

        var name = new System.Text.StringBuilder(member.BrowseName);
        var remainingSteps = member.Parent.Properties.Length;
        var current = member;
        while (current.IsAttribute)
        {
            var property = current.Parent.TryGetProperty(current.AttributeMetadata.PropertyName);
            if (property is null || --remainingSteps < 0)
            {
                return null;
            }

            name.Insert(0, '@').Insert(0, property.IsAttribute ? property.BrowseName : property.Name);
            current = property;
        }

        return name.ToString();
    }

    private static bool HasAllTags(ImmutableArray<string> memberTags, string[] requiredTags)
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

    private static bool ContainsTag(ImmutableArray<string> memberTags, string tag)
    {
        foreach (var memberTag in memberTags)
        {
            if (string.Equals(memberTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Every path of the subject as of one instant of the graph; a root has the single empty path. When the graph changed
    // during every attempt, no path is listed, so an allow cannot match a path that never existed. In a subject
    // evaluation the reads are only recorded, for the evaluation to check, and each subject's paths are built once per
    // attempt.
    private PathList GetPaths(RegisteredSubject subject, ref MatchContext context)
    {
        if (context.SharedState is { } sharedState)
        {
            return sharedState.GetPaths(subject, _pathProvider!, MaxPaths, out context.ReleasablePathsStart);
        }

        // The answer for a root or a detached subject rests on one subject's parents and attachment, which hold together
        // at the first read (the attachment only goes from true to false), so nothing needs to be checked again.
        var parents = subject.Parents;
        var isAttached = subject.IsAttached;
        if (!isAttached || parents.Length == 0)
        {
            return isAttached ? PathList.Root : PathList.Unknown;
        }

        // The paths live in the state, so the context keeps it until the question is answered.
        var state = context.RentedState ??= EvaluationState.Rent();
        for (var attempt = 0; attempt < MaxConsistencyAttempts; attempt++)
        {
            if (attempt > 0)
            {
                state.Clear();
            }

            var paths = state.BuildPaths(subject, _pathProvider!, MaxPaths);
            if (state.Reads.AreUnchanged())
            {
                return paths;
            }
        }

        return PathList.Unknown;
    }

    // Also hands out the subject's paths when a rule needed them, for the walk to derive its owners' paths from; none otherwise.
    private bool HasReadableMember(RegisteredSubject subject, EvaluationState state, out PathList subjectPaths)
    {
        var context = new MatchContext { SharedState = state };
        foreach (var member in subject.Properties)
        {
            context.SetMember(member);
            if (IsAllowed(PolicyCompiler.ReadBit, ref context))
            {
                subjectPaths = context.HasPaths ? context.Paths : default;
                return true;
            }
        }

        subjectPaths = default;
        return false;
    }

    // True when some chain of readable subject-holding properties of attached subjects leads to a root. Otherwise
    // deniedProperty is the first unreadable link found in depth-first order, or stays null when every chain returns to
    // a subject already walked or passes a subject that is no longer attached. Iterative, so a deep graph cannot
    // overflow the stack; a cursor with untried parents waits in pending, which a single-parent chain never needs.
    private bool IsReachable(RegisteredSubject subject, EvaluationState state, PathList subjectPaths, ref RegisteredSubjectProperty? deniedProperty)
    {
        HashSet<RegisteredSubject>? visited = null;
        List<ParentCursor>? pending = null;
        var hasCursor = TryEnter(subject, 0, state, ref visited, out var cursor, out var isRoot);
        var cursorPaths = subjectPaths;
        var context = new MatchContext { SharedState = state };
        while (!isRoot)
        {
            if (!hasCursor)
            {
                if (pending is null || pending.Count == 0)
                {
                    return false;
                }

                cursor = pending[^1];
                pending.RemoveAt(pending.Count - 1);

                // A waiting cursor's subject has several parents, so its paths give no owner's paths.
                cursorPaths = default;
            }

            var parent = cursor.Parents[cursor.Next];
            if (cursor.Next + 1 < cursor.Parents.Length)
            {
                (pending ??= []).Add(cursor with { Next = cursor.Next + 1 });
            }

            if (!IsLinkReadable(ref context, parent.Property, cursor, ref cursorPaths))
            {
                deniedProperty ??= parent.Property;
                hasCursor = false;
                continue;
            }

            hasCursor = TryEnter(parent.Property.Parent, cursor.Depth + 1, state, ref visited, out cursor, out isRoot);
        }

        return true;
    }

    // Enters a subject: isRoot when it is an attached subject without parents; no cursor when it is no longer attached
    // or was already explored.
    private static bool TryEnter(
        RegisteredSubject subject, int depth, EvaluationState state, ref HashSet<RegisteredSubject>? visited, out ParentCursor cursor, out bool isRoot)
    {
        cursor = default;
        isRoot = false;
        var parents = state.Reads.GetParents(subject, out var isAttached);
        if (!isAttached)
        {
            return false;
        }

        if (parents.Length == 0)
        {
            isRoot = true;
            return false;
        }

        // Already explored from here, or a cycle: no new route to a root through this subject.
        if ((parents.Length > 1 || depth >= VisitedTrackingDepth) && !(visited ??= []).Add(subject))
        {
            return false;
        }

        cursor = new ParentCursor(parents, 0, depth);
        return true;
    }

    // Checks the link to the cursor's next parent and replaces cursorPaths by the owner's single chain, if it has one.
    // The owner of a single chain's last link has that chain without the link, exactly what its own enumeration builds,
    // so a deep chain is not enumerated once per link. Any other paths could differ from the owner's, if only in
    // whether the search budget lasted, so the owner's are built and released after the check.
    private bool IsLinkReadable(ref MatchContext context, RegisteredSubjectProperty link, in ParentCursor cursor, ref PathList cursorPaths)
    {
        context.SetMember(link);
        context.HasPaths = cursor.Parents.Length == 1 && cursorPaths.IsSingleChain;
        context.Paths = context.HasPaths ? cursorPaths.WithoutLastLink() : default;
        context.ReleasablePathsStart = -1;

        var isReadable = IsAllowed(PolicyCompiler.ReadBit, ref context);
        if (!context.HasPaths)
        {
            cursorPaths = default;
        }
        else if (context.Paths.IsSingleChain)
        {
            cursorPaths = context.Paths;
        }
        else
        {
            cursorPaths = default;
            if (context.ReleasablePathsStart >= 0)
            {
                context.SharedState!.ReleasePaths(context.ReleasablePathsStart);
            }
        }

        return isReadable;
    }

    private readonly record struct ParentCursor(ImmutableArray<SubjectPropertyParent> Parents, int Next, int Depth);

    // The facts one question derives, each at most once and only when a rule needs it.
    private struct MatchContext
    {
        public RegisteredSubjectProperty Member;
        public string? MemberName;
        public bool IsMemberNameResolved;
        public ImmutableArray<string> Tags;
        public string? MemberSegment;
        public PathList Paths;
        public bool HasPaths;

        // Where the paths this question built start in the shared state, when no later question uses them; -1 otherwise.
        public int ReleasablePathsStart;

        // When set, the question is part of a subject evaluation: the paths' parent reads are recorded here for the
        // evaluation to check, instead of being checked per enumeration.
        public EvaluationState? SharedState;

        // The state a question on its own rented for its paths, which point into it until the question is answered.
        public EvaluationState? RentedState;

        // The paths belong to the subject, not the member, so they are kept for the next member of the same subject.
        public void SetMember(RegisteredSubjectProperty member)
        {
            Member = member;
            MemberName = null;
            IsMemberNameResolved = false;
            Tags = default;
            MemberSegment = null;
        }

        public void ReturnRentedState()
        {
            if (RentedState is not null)
            {
                EvaluationState.Return(RentedState);
                RentedState = null;
            }
        }
    }
}

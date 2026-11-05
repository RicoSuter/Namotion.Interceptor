using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization;

/// <summary>
/// Tells the members that one deny rule matches, whatever the other rules and the system rules decide, as a policy
/// names only the first rule that decided an action. A member matches as it would in a policy: also when its paths
/// cannot all be listed and the rule has paths. Immutable and thread-safe.
/// </summary>
public sealed class DenyRuleMatcher
{
    private const string ProbeRoleName = "DenyRuleProbe";

    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    private static readonly PermissionRule AllowEverything = new() { Actions = AuthorizationActions.All };

    private readonly AuthorizationPolicy _policy;
    private readonly PermissionAction _action;
    private readonly bool _appliesToMethods;
    private readonly bool _appliesToValues;

    private DenyRuleMatcher(PermissionRule rule, AuthorizationPolicy policy)
    {
        _policy = policy;
        _action = rule.Actions[0];
        _appliesToMethods = rule.Actions.Any(action => AuthorizationActions.AppliesTo(action, isMethod: true));
        _appliesToValues = rule.Actions.Any(action => AuthorizationActions.AppliesTo(action, isMethod: false));
    }

    /// <summary>
    /// Creates the matcher of <paramref name="rule"/>, or null when the rule does not compile, such as for an unknown
    /// action or an invalid path pattern.
    /// </summary>
    public static DenyRuleMatcher? Create(PermissionRule rule, PathProviderBase pathProvider)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(pathProvider);

        // Allowed everything and denied the rule alone, without system rules, a member is denied exactly when the rule
        // matches it; the selectors are the same for each action of the rule, so one action answers for all.
        var role = new AuthorizationRole { Name = ProbeRoleName, Allow = [AllowEverything], Deny = [rule] };
        try
        {
            return new DenyRuleMatcher(rule, AuthorizationPolicy.Compile([role], [ProbeRoleName], [], AuthorizationActions.All, TagProvider, pathProvider));
        }
        catch (AuthorizationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the rule matches <paramref name="member"/> for an action that applies to it, see
    /// <see cref="AuthorizationActions.AppliesTo"/>.
    /// </summary>
    public bool Matches(RegisteredSubjectProperty member)
    {
        ArgumentNullException.ThrowIfNull(member);

        // Methods are registered as properties of this type; no other getter is called.
        var isMethod = member.Type == typeof(MethodMetadata);
        return (isMethod ? _appliesToMethods : _appliesToValues) && !_policy.IsAllowed(member, _action);
    }

    /// <summary>Whether the rule matches a member of one of <paramref name="subjects"/>, see <see cref="Matches"/>.</summary>
    public bool MatchesAny(IEnumerable<RegisteredSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        return subjects.Any(subject => subject.Properties.Any(Matches));
    }
}

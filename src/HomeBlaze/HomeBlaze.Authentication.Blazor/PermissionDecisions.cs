using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// What a principal may see and do, decided with its compiled policy and the scope of administration: the single place of
/// these rules for <see cref="PolicyUserPermissions"/> and for the permissions of a request outside a circuit, see
/// <see cref="RequestUserPermissionsFactory"/>. Immutable, so its answers never change.
/// </summary>
internal sealed class PermissionDecisions : IUserPermissions
{
    // The property name of Authorization.AddUserAsync, which method properties register without the suffix.
    private const string AddUserMemberName = "AddUser";

    // The render thread and the menu timer of a circuit.
    private const int ShownMemoConcurrencyLevel = 2;

    private readonly AuthorizationService? _authorizationService;

    private readonly AuthorizationPolicy _policy;

    private readonly IReadOnlySet<string> _roleClosure;

    private readonly IReadOnlySet<string> _rolesWithDenyRules;

    private readonly bool _isPerson;

    // Replaced when the graph version changed; an older memo may still receive the answers of a reader that started
    // before, which no reader of the newer version looks at.
    private volatile ShownMemo? _shownMemo;

    // The most subjects a memo answered for, which sizes the next one, so that it does not grow while it fills.
    private int _shownCount;

    // Created when first needed: only a principal with roles with deny rules asks for the closure of other roles.
    private ConditionalWeakTable<IReadOnlyCollection<string>, IReadOnlyList<string>>? _missingRolesWithDenyRules;

    private PermissionDecisions(
        AuthorizationService? authorizationService,
        AuthorizationPolicy policy,
        IReadOnlySet<string> roleClosure,
        IReadOnlySet<string> rolesWithDenyRules,
        bool isPerson,
        bool isFullAdministrator,
        int shownCount)
    {
        _authorizationService = authorizationService;
        _policy = policy;
        _roleClosure = roleClosure;
        _rolesWithDenyRules = rolesWithDenyRules;
        _isPerson = isPerson;
        IsFullAdministrator = isFullAdministrator;
        _shownCount = shownCount;
    }

    /// <summary>Decisions that deny everything.</summary>
    public static PermissionDecisions DenyAll { get; } =
        new(null, AuthorizationPolicy.DenyAll, new HashSet<string>(), new HashSet<string>(), isPerson: false, isFullAdministrator: false, shownCount: 0);

    /// <summary>Whether the principal is a full administrator, a person for whom <see cref="AuthorizationServiceRoleExtensions.IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string})"/> holds.</summary>
    public bool IsFullAdministrator { get; }

    /// <summary>
    /// Creates the decisions of a principal of <paramref name="kind"/> holding <paramref name="roles"/>; they deny
    /// everything when the roles do not compile. <paramref name="replaced"/>, the decisions they replace, sizes what
    /// they remember.
    /// </summary>
    public static PermissionDecisions Create(
        AuthorizationService authorizationService, IReadOnlyCollection<string> roles, PrincipalKind kind, PermissionDecisions? replaced = null)
    {
        var policy = authorizationService.GetPolicy(roles, kind).Policy;
        var roleClosure = authorizationService.GetRoleClosure(roles);
        var isPerson = kind == PrincipalKind.Person;
        return new PermissionDecisions(
            authorizationService,
            policy,
            roleClosure,
            authorizationService.GetRolesWithDenyRules(roleClosure),
            isPerson,
            isPerson && authorizationService.IsFullAdministrator(roles, roleClosure),
            replaced?.GetShownCount() ?? 0);
    }

    /// <summary>
    /// Creates the decisions of a principal from the inputs of its policy: those of
    /// <see cref="Create(AuthorizationService, IReadOnlyCollection{string}, PrincipalKind, PermissionDecisions?)"/>, and
    /// <see cref="DenyAll"/> for a person who must change their password.
    /// </summary>
    public static PermissionDecisions Create(
        AuthorizationService authorizationService,
        (IReadOnlyCollection<string> Roles, PrincipalKind Kind, bool MustChangePassword) inputs,
        PermissionDecisions? replaced = null) =>
        inputs.MustChangePassword ? DenyAll : Create(authorizationService, inputs.Roles, inputs.Kind, replaced);

    /// <summary>Never raised: the decisions do not change.</summary>
    public event Action? Changed
    {
        add
        {
            // Nothing to subscribe to: the decisions never change.
        }

        remove
        {
            // Nothing was subscribed.
        }
    }

    /// <summary>
    /// Gets the value of the member as the principal is shown it: the order in the metadata of a folder names only the
    /// children the principal is shown.
    /// </summary>
    public object? GetValue(RegisteredSubjectProperty member) =>
        ChildOrdering.FilterOrderInMetadata(
            member,
            member.GetValue(),
            this,
            static (decisions, folder) => ChildOrdering.GetListedNames(
                folder,
                decisions,
                static (shownDecisions, _, child) => child.Subject.TryGetRegisteredSubject() is { } registered && shownDecisions.CanShow(registered)));

    /// <summary>Whether the principal holds every role, counting the roles they include, or is a full administrator; false for an empty set.</summary>
    public bool HoldsAllRoles(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            return false;
        }

        if (IsFullAdministrator)
        {
            return true;
        }

        foreach (var role in roles)
        {
            if (!_roleClosure.Contains(role))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the principal may act through a principal holding <paramref name="roles"/>: it holds all of them, and they
    /// hold each of its roles with deny rules, see <see cref="RoleDelegation"/>; for an empty set, whether it is a full
    /// administrator.
    /// </summary>
    public bool CanActThrough(IReadOnlyCollection<string> roles) =>
        roles.Count > 0 ? HoldsAllRoles(roles) && GetRolesWithDenyRulesMissingFrom(roles).Count == 0 : IsFullAdministrator;

    /// <summary>Gets the roles of the principal with deny rules that <paramref name="roles"/>, counting the roles they include, lack.</summary>
    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles)
    {
        // Only a principal with such roles needs the closure of the others, so that the usual answer allocates nothing.
        if (_rolesWithDenyRules.Count == 0 || roles.Count == 0 || _authorizationService is null)
        {
            return [];
        }

        // Only an immutable set can be remembered by reference, such as the scope roles of an agent.
        if (roles is not ImmutableArray<string>)
        {
            return RoleDelegation.GetMissingRolesWithDenyRules(_rolesWithDenyRules, _authorizationService.GetRoleClosure(roles));
        }

        var remembered = LazyInitializer.EnsureInitialized(ref _missingRolesWithDenyRules);
        if (!remembered.TryGetValue(roles, out var missing))
        {
            missing = RoleDelegation.GetMissingRolesWithDenyRules(_rolesWithDenyRules, _authorizationService.GetRoleClosure(roles));
            remembered.AddOrUpdate(roles, missing);
        }

        return missing;
    }

    /// <summary>
    /// Whether the member may be read: as the policy allows, or a member of a user the person may administer; never a
    /// member that its subject shares only with people who may act through its roles, see <see cref="IRoleScopedSubject"/>.
    /// </summary>
    public bool CanRead(RegisteredSubjectProperty member) =>
        IsSharedWithPrincipal(member) && (_policy.CanRead(member) || IsAdministeredUserMember(member));

    /// <summary>Whether the member may be written: as the policy allows, within the scope of <see cref="IRoleScopedSubject"/>.</summary>
    public bool CanWrite(RegisteredSubjectProperty member) => IsSharedWithPrincipal(member) && _policy.CanWrite(member);

    /// <summary>
    /// Whether the history of the member may be read: as the policy allows, or a member of a user the person may
    /// administer, within the scope of <see cref="IRoleScopedSubject"/>.
    /// </summary>
    public bool CanReadHistory(RegisteredSubjectProperty member) =>
        IsSharedWithPrincipal(member) && (_policy.IsAllowed(member, AuthorizationActions.History) || IsAdministeredUserMember(member));

    /// <summary>
    /// Whether the subject may be shown: as the policy allows, or a user of whom the person may administer a member and
    /// whose parent the policy shows through a readable property.
    /// </summary>
    /// <remarks>Remembered per subject until <see cref="AuthorizationService.GraphVersion"/> changes.</remarks>
    public bool CanShow(RegisteredSubject subject)
    {
        if (_authorizationService is null)
        {
            return DecideCanShow(subject);
        }

        // Read before the graph, so that an answer decided while the graph changes is kept for the older version only.
        var graphVersion = _authorizationService.GraphVersion;
        var memo = _shownMemo;
        if (memo is null || memo.GraphVersion != graphVersion)
        {
            var shownCount = GetShownCount();

            // With headroom, as a memo grows when the subjects of one of its locks exceed their share.
            memo = new ShownMemo(graphVersion, shownCount + shownCount / 4);
            _shownMemo = memo;
        }

        if (memo.Answers.TryGetValue(subject, out var isShown))
        {
            return isShown;
        }

        isShown = DecideCanShow(subject);
        memo.Answers.TryAdd(subject, isShown);
        return isShown;
    }

    private bool DecideCanShow(RegisteredSubject subject) => _policy.IsVisible(subject) || IsShownAdministeredUser(subject);

    private int GetShownCount()
    {
        if (_shownMemo is { } memo)
        {
            // Unsynchronized on purpose: a lost update only misjudges the size of the next memo.
            _shownCount = Math.Max(_shownCount, memo.Answers.Count);
        }

        return _shownCount;
    }

    /// <summary>
    /// Whether the method of the member may run: <c>administer</c> when it requires administration, else <c>execute</c>,
    /// within the scope of <see cref="IRoleScopedSubject"/>.
    /// </summary>
    public bool CanInvoke(RegisteredSubjectProperty member) =>
        IsSharedWithPrincipal(member) &&
        (RequiresAdministration(member) ? CanAdminister(member) : _policy.IsAllowed(member, AuthorizationActions.Execute));

    public bool CanAdminister(RegisteredSubjectProperty member)
    {
        if (!_policy.IsAllowed(member, AuthorizationActions.Administer))
        {
            return false;
        }

        // Only user management honors a scoped grant in this version; every other administration needs a full administrator.
        return IsFullAdministrator || IsUserManagementMember(member);
    }

    // The system rules hide users from people; the person-facing decisions show them to those who may administer them,
    // so the policy, which agents and MCP clients share, stays unchanged.
    private bool IsAdministeredUserMember(RegisteredSubjectProperty member) =>
        _isPerson && member.Subject is User && CanAdminister(member);

    private bool IsShownAdministeredUser(RegisteredSubject subject)
    {
        if (!_isPerson || subject.Subject is not User)
        {
            return false;
        }

        var parents = subject.Parents;

        // Read after the parents, as the policy does: the registry clears it before it removes the last parent.
        if (!subject.IsAttached || !CanAdministerAnyMember(subject))
        {
            return false;
        }

        foreach (var parent in parents)
        {
            if (_policy.CanRead(parent.Property) && _policy.IsVisible(parent.Property.Parent))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanAdministerAnyMember(RegisteredSubject subject)
    {
        foreach (var member in subject.Properties)
        {
            if (CanAdminister(member))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSharedWithPrincipal(RegisteredSubjectProperty member)
    {
        if (member.Subject is not IRoleScopedSubject scoped || !scoped.IsRoleScoped(member.Name))
        {
            return true;
        }

        return CanActThrough(scoped.ScopeRoles);
    }

    private static bool IsUserManagementMember(RegisteredSubjectProperty member) =>
        member.Subject is User ||
        (member.Subject is AuthorizationSubject && member.Name is nameof(AuthorizationSubject.Users) or AddUserMemberName);

    // Only a property of the type that methods are registered with is read, so that the getter of another property,
    // which may be expensive or throw, is never called.
    private static bool RequiresAdministration(RegisteredSubjectProperty member) =>
        member.Type == typeof(MethodMetadata) && member.GetValue() is MethodMetadata { RequiresAdministration: true };

    private sealed class ShownMemo(long graphVersion, int capacity)
    {
        public long GraphVersion { get; } = graphVersion;

        public ConcurrentDictionary<RegisteredSubject, bool> Answers { get; } =
            new(ShownMemoConcurrencyLevel, capacity, ReferenceEqualityComparer.Instance);
    }
}

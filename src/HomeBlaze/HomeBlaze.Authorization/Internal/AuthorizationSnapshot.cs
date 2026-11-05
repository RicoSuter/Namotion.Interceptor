using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The roles of the graph at one version, with the policies compiled from them and the statuses they give the
/// <see cref="Authorization"/> and <see cref="Role"/> subjects. Immutable apart from the caches of policies and
/// previews, which are thread-safe.
/// </summary>
internal sealed class AuthorizationSnapshot
{
    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    /// <summary>The status of every <see cref="Authorization"/> and of their roles while several exist.</summary>
    internal const string SeveralAuthorizationsStatus =
        "Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.";

    private readonly PathProviderBase _pathProvider;
    private readonly ImmutableArray<AuthorizationRole> _roles;
    private readonly Dictionary<string, string> _unavailableRoles;
    private readonly string? _customRolesError;
    private readonly ConcurrentDictionary<RoleNameSet, AuthorizationResult>[] _policies = CreatePolicyCaches();
    private readonly PreviewCache _previews = new();

    // The error of each role with a path pattern the application refuses, see PathPatternChecks; null when there is none.
    private readonly Dictionary<string, string>? _leadingSeparatorErrors;

    private static ConcurrentDictionary<RoleNameSet, AuthorizationResult>[] CreatePolicyCaches()
    {
        var caches = new ConcurrentDictionary<RoleNameSet, AuthorizationResult>[Enum.GetValues<PrincipalKind>().Length];
        for (var index = 0; index < caches.Length; index++)
        {
            caches[index] = new();
        }

        return caches;
    }

    private AuthorizationSnapshot(
        long version,
        PathProviderBase pathProvider,
        ImmutableArray<AuthorizationRole> roles,
        Dictionary<string, string> unavailableRoles,
        string? customRolesError)
    {
        Version = version;
        _pathProvider = pathProvider;
        _roles = roles;
        _unavailableRoles = unavailableRoles;
        _customRolesError = customRolesError;
        _leadingSeparatorErrors = PathPatternChecks.GetLeadingSeparatorErrors(roles, pathProvider.PathSeparator);
        RoleNames = ImmutableArray.CreateRange(roles, static role => role.Name);
    }

    /// <summary>The version of the graph's roles this snapshot was built from.</summary>
    public long Version { get; }

    /// <summary>The names of the built-in roles, then of the usable custom roles ordered by name.</summary>
    public IReadOnlyList<string> RoleNames { get; }

    /// <summary>The custom roles that exist but cannot be used, by name, with the reason.</summary>
    public IReadOnlyDictionary<string, string> UnavailableRoles => _unavailableRoles;

    /// <summary>The status of each <see cref="Authorization"/> subject.</summary>
    public ImmutableArray<(Authorization Authorization, string? Status)> AuthorizationStatuses { get; private set; } = [];

    /// <summary>The status and warning of each <see cref="Role"/> subject, with its name.</summary>
    public ImmutableArray<(Role Role, string Name, string? Status, string? Warning)> RoleStatuses { get; private set; } = [];

    /// <summary>
    /// Builds the snapshot from the <see cref="Authorization"/> subjects among <paramref name="knownSubjects"/>, compiling
    /// each usable custom role on its own for its status, and checking the path patterns of each that compiles against
    /// the graph for its warning.
    /// </summary>
    public static AuthorizationSnapshot Build(
        long version,
        IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> knownSubjects,
        PathProviderBase pathProvider)
    {
        var authorizations = knownSubjects.Keys.OfType<Authorization>().ToList();
        var authorizationStatuses = ImmutableArray.CreateBuilder<(Authorization, string?)>(authorizations.Count);
        var roleStatuses = ImmutableArray.CreateBuilder<(Role, string, string?, string?)>();
        var unavailableRoles = new Dictionary<string, string>(StringComparer.Ordinal);
        var customRoles = new List<KeyValuePair<string, Role>>();
        string? customRolesError = null;

        if (authorizations.Count > 1)
        {
            customRolesError = SeveralAuthorizationsStatus;
            foreach (var authorization in authorizations)
            {
                authorizationStatuses.Add((authorization, customRolesError));
                foreach (var (name, role) in GetRoles(authorization))
                {
                    roleStatuses.Add((role, name, customRolesError, null));
                    if (!BuiltInRoles.IsBuiltIn(name))
                    {
                        unavailableRoles.TryAdd(name, customRolesError);
                    }
                }
            }
        }
        else if (authorizations.Count == 1)
        {
            authorizationStatuses.Add((authorizations[0], null));
            var roles = GetRoles(authorizations[0]);
            var countsByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, _) in roles)
            {
                countsByName[name] = countsByName.GetValueOrDefault(name) + 1;
            }

            foreach (var entry in roles)
            {
                var nameError = GetNameError(entry.Key, countsByName[entry.Key]);
                if (nameError is null)
                {
                    customRoles.Add(entry);
                    continue;
                }

                roleStatuses.Add((entry.Value, entry.Key, nameError, null));
                if (!BuiltInRoles.IsBuiltIn(entry.Key))
                {
                    unavailableRoles.TryAdd(entry.Key, nameError);
                }
            }
        }

        customRoles.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        var allRoles = ImmutableArray.CreateBuilder<AuthorizationRole>(BuiltInRoles.All.Length + customRoles.Count);
        allRoles.AddRange(BuiltInRoles.All);
        foreach (var (name, role) in customRoles)
        {
            allRoles.Add(ToAuthorizationRole(name, role));
        }

        var snapshot = new AuthorizationSnapshot(version, pathProvider, allRoles.MoveToImmutable(), unavailableRoles, customRolesError);

        snapshot.AddCustomRoleStatuses(customRoles, knownSubjects, roleStatuses);
        snapshot.AuthorizationStatuses = authorizationStatuses.ToImmutable();
        snapshot.RoleStatuses = roleStatuses.ToImmutable();
        return snapshot;
    }

    /// <summary>
    /// Builds a snapshot of the built-in roles and <paramref name="customRoles"/>, as the complete set of custom roles,
    /// to compile policies with; a custom role whose name cannot be used is unavailable. Its version is
    /// <see cref="UnreadableVersion"/>, and it has no statuses.
    /// </summary>
    public static AuthorizationSnapshot BuildCandidate(IReadOnlyList<AuthorizationRole> customRoles, PathProviderBase pathProvider)
    {
        var countsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var role in customRoles)
        {
            countsByName[role.Name] = countsByName.GetValueOrDefault(role.Name) + 1;
        }

        var unavailableRoles = new Dictionary<string, string>(StringComparer.Ordinal);
        var usableRoles = new List<AuthorizationRole>(customRoles.Count);
        foreach (var role in customRoles)
        {
            if (GetNameError(role.Name, countsByName[role.Name]) is not { } nameError)
            {
                usableRoles.Add(role);
            }
            else if (!BuiltInRoles.IsBuiltIn(role.Name))
            {
                unavailableRoles.TryAdd(role.Name, nameError);
            }
        }

        usableRoles.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        var allRoles = ImmutableArray.CreateBuilder<AuthorizationRole>(BuiltInRoles.All.Length + usableRoles.Count);
        allRoles.AddRange(BuiltInRoles.All);
        allRoles.AddRange(usableRoles);
        return new AuthorizationSnapshot(UnreadableVersion, pathProvider, allRoles.MoveToImmutable(), unavailableRoles, customRolesError: null);
    }

    /// <summary>The version of a snapshot that could not read the roles, which no version of the graph has.</summary>
    public const long UnreadableVersion = -1;

    /// <summary>
    /// Creates a snapshot of the built-in roles for when the roles of the graph could not be read: every custom role is
    /// unavailable, and no status is written. Its version is <see cref="UnreadableVersion"/>, so it is always stale.
    /// </summary>
    public static AuthorizationSnapshot CreateUnreadable(PathProviderBase pathProvider) =>
        new(UnreadableVersion, pathProvider, BuiltInRoles.All, new Dictionary<string, string>(StringComparer.Ordinal),
            "The roles changed while they were read; try again.");

    /// <summary>Gets the cached or newly compiled policy of a principal holding the role names.</summary>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames, PrincipalKind kind)
    {
        if (roleNames.Count == 0)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, "No roles are assigned.");
        }

        var key = RoleNameSet.TryCreate(roleNames);
        if (key is null)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, "A role name is empty.");
        }

        var policies = _policies[(int)kind];
        if (policies.TryGetValue(key, out var cached))
        {
            return cached;
        }

        return policies.GetOrAdd(key, Compile(key.Names, kind));
    }

    /// <summary>
    /// Compiles the policy of a principal holding only <paramref name="editedRole"/>, which replaces the custom role of
    /// the same name, or is added. Only the result of the last previewed instance is kept; its error is the
    /// status the role would have once saved.
    /// </summary>
    public AuthorizationResult Preview(AuthorizationRole editedRole, PrincipalKind kind)
    {
        if (!_previews.TryGet(editedRole, kind, out var result))
        {
            result = CompilePreview(editedRole, kind);
            _previews.Set(editedRole, kind, result);
        }

        return result;
    }

    private AuthorizationResult CompilePreview(AuthorizationRole editedRole, PrincipalKind kind)
    {
        var name = editedRole.Name;
        var reason = GetNameError(name, count: 1) ?? _unavailableRoles.GetValueOrDefault(name) ?? _customRolesError;
        if (reason is not null)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, reason);
        }

        var index = IndexOfRole(name);
        var roles = index >= 0 ? _roles.SetItem(index, editedRole) : _roles.Add(editedRole);
        return Compile(roles, [name], PathPatternChecks.GetLeadingSeparatorErrors(roles, _pathProvider.PathSeparator), kind);
    }

    private AuthorizationResult Compile(string[] roleNames, PrincipalKind kind)
    {
        foreach (var name in roleNames)
        {
            var reason = _unavailableRoles.GetValueOrDefault(name) ??
                (BuiltInRoles.IsBuiltIn(name) ? null : _customRolesError);
            if (reason is not null)
            {
                return new AuthorizationResult(AuthorizationPolicy.DenyAll, $"Role '{name}' is unavailable: {reason}");
            }
        }

        return Compile(_roles, roleNames, _leadingSeparatorErrors, kind);
    }

    private AuthorizationResult Compile(
        ImmutableArray<AuthorizationRole> roles, string[] roleNames, Dictionary<string, string>? leadingSeparatorErrors, PrincipalKind kind)
    {
        if (leadingSeparatorErrors is not null &&
            PathPatternChecks.FindLeadingSeparatorError(roleNames, roles, leadingSeparatorErrors) is { } patternError)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, patternError);
        }

        try
        {
            var policy = AuthorizationPolicy.Compile(
                roles, roleNames, SystemRules.For(kind), AuthorizationActions.All, TagProvider, _pathProvider);
            return new AuthorizationResult(policy, null);
        }
        catch (AuthorizationException exception)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, exception.Message);
        }
    }

    /// <summary>Gets the names the role named <paramref name="name"/>, compared ordinally, includes, or null when there is no such role.</summary>
    public IReadOnlyList<string>? TryGetIncludes(string name)
    {
        var index = IndexOfRole(name);
        return index >= 0 ? _roles[index].Includes : null;
    }

    /// <summary>Whether the role named <paramref name="name"/> has a deny rule of its own, or null when there is no such usable role.</summary>
    public bool? HasDenyRules(string name)
    {
        var index = IndexOfRole(name);
        return index >= 0 ? !_roles[index].Deny.IsDefaultOrEmpty : null;
    }

    private int IndexOfRole(string name)
    {
        for (var index = 0; index < _roles.Length; index++)
        {
            if (string.Equals(_roles[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    // Each usable role compiled on its own gives its status and is the policy of a principal holding only it.
    private void AddCustomRoleStatuses(
        List<KeyValuePair<string, Role>> customRoles,
        IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> knownSubjects,
        ImmutableArray<(Role, string, string?, string?)>.Builder roleStatuses)
    {
        List<RegisteredSubject>? roots = null;
        for (var i = 0; i < customRoles.Count; i++)
        {
            var (name, role) = customRoles[i];
            var error = GetPolicy([name], PrincipalKind.NonHuman).Error;
            var warning = error is null
                ? PathPatternChecks.GetWarning(_roles[BuiltInRoles.All.Length + i], knownSubjects, _pathProvider, ref roots)
                : null;
            roleStatuses.Add((role, name, error, warning));
        }
    }

    private static List<KeyValuePair<string, Role>> GetRoles(Authorization authorization)
    {
        var roles = new List<KeyValuePair<string, Role>>();
        if (authorization.Roles is { } entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Value is not null)
                {
                    roles.Add(entry);
                }
            }
        }

        return roles;
    }

    /// <summary>
    /// Gets why <paramref name="name"/> cannot name a custom role held by <paramref name="count"/> roles of the
    /// <see cref="Authorization"/>, or null, see <see cref="AuthorizationService.GetRoleNameError"/>.
    /// </summary>
    public static string? GetNameError(string name, int count)
    {
        if (NameRules.GetNameError(name, "role name") is { } nameError)
        {
            return nameError;
        }

        if (BuiltInRoles.IsBuiltIn(name))
        {
            return $"The name '{name}' is reserved for a built-in role.";
        }

        return count > 1 ? $"The name '{name}' is used by more than one role." : null;
    }

    private static AuthorizationRole ToAuthorizationRole(string name, Role role) =>
        new()
        {
            Name = name,
            Includes = role.Includes is { } includes ? [.. includes] : [],
            Allow = role.Allow is { } allow ? [.. allow] : [],
            Deny = role.Deny is { } deny ? [.. deny] : []
        };
}

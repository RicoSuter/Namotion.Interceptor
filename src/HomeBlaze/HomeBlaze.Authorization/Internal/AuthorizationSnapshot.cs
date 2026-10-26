using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The roles of the graph at one version, with the policies compiled from them and the statuses they give the
/// <see cref="Authorization"/> and <see cref="Role"/> subjects. Immutable apart from the policy cache, which is thread-safe.
/// </summary>
internal sealed class AuthorizationSnapshot
{
    private static readonly HomeBlazeMemberTagProvider TagProvider = new();

    private readonly PathProviderBase _pathProvider;
    private readonly ImmutableArray<AuthorizationRole> _roles;
    private readonly Dictionary<string, string> _unavailableRoles;
    private readonly string? _customRolesError;
    private readonly ConcurrentDictionary<RoleNameSet, AuthorizationResult> _policies = new();

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
        RoleNames = ImmutableArray.CreateRange(roles, static role => role.Name);
    }

    /// <summary>The version of the graph's roles this snapshot was built from.</summary>
    public long Version { get; }

    /// <summary>The names of the built-in roles, then of the usable custom roles ordered by name.</summary>
    public IReadOnlyList<string> RoleNames { get; }

    /// <summary>The status of each <see cref="Authorization"/> subject.</summary>
    public ImmutableArray<(Authorization Authorization, string? Status)> AuthorizationStatuses { get; private set; } = [];

    /// <summary>The status of each <see cref="Role"/> subject, with its name.</summary>
    public ImmutableArray<(Role Role, string Name, string? Status)> RoleStatuses { get; private set; } = [];

    /// <summary>
    /// Builds the snapshot from the <see cref="Authorization"/> subjects among <paramref name="knownSubjects"/>, compiling
    /// each usable custom role on its own for its status.
    /// </summary>
    public static AuthorizationSnapshot Build(long version, IEnumerable<IInterceptorSubject> knownSubjects, PathProviderBase pathProvider)
    {
        var authorizations = knownSubjects.OfType<Authorization>().ToList();
        var authorizationStatuses = ImmutableArray.CreateBuilder<(Authorization, string?)>(authorizations.Count);
        var roleStatuses = ImmutableArray.CreateBuilder<(Role, string, string?)>();
        var unavailableRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var customRoles = new List<KeyValuePair<string, Role>>();
        string? customRolesError = null;

        if (authorizations.Count > 1)
        {
            customRolesError = AuthorizationService.SeveralAuthorizationsStatus;
            foreach (var authorization in authorizations)
            {
                authorizationStatuses.Add((authorization, customRolesError));
                foreach (var (name, role) in GetRoles(authorization))
                {
                    roleStatuses.Add((role, name, customRolesError));
                }
            }
        }
        else if (authorizations.Count == 1)
        {
            authorizationStatuses.Add((authorizations[0], null));
            var roles = GetRoles(authorizations[0]);
            var countsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

                roleStatuses.Add((entry.Value, entry.Key, nameError));
                if (!BuiltInRoles.IsBuiltIn(entry.Key))
                {
                    unavailableRoles.TryAdd(entry.Key, nameError);
                }
            }
        }

        customRoles.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key));

        var allRoles = ImmutableArray.CreateBuilder<AuthorizationRole>(BuiltInRoles.All.Length + customRoles.Count);
        allRoles.AddRange(BuiltInRoles.All);
        foreach (var (name, role) in customRoles)
        {
            allRoles.Add(ToAuthorizationRole(name, role));
        }

        var snapshot = new AuthorizationSnapshot(version, pathProvider, allRoles.MoveToImmutable(), unavailableRoles, customRolesError);

        // Each usable role compiled on its own gives its status and is the policy of a principal holding only it.
        foreach (var (name, role) in customRoles)
        {
            roleStatuses.Add((role, name, snapshot.GetPolicy([name]).Error));
        }

        snapshot.AuthorizationStatuses = authorizationStatuses.ToImmutable();
        snapshot.RoleStatuses = roleStatuses.ToImmutable();
        return snapshot;
    }

    /// <summary>Gets the cached or newly compiled policy of a principal holding the role names.</summary>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames)
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

        if (_policies.TryGetValue(key, out var cached))
        {
            return cached;
        }

        return _policies.GetOrAdd(key, Compile(key.Names));
    }

    private AuthorizationResult Compile(string[] roleNames)
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

        try
        {
            var policy = AuthorizationPolicy.Compile(
                _roles, roleNames, SystemRules.Rules, AuthorizationActions.All, TagProvider, _pathProvider);
            return new AuthorizationResult(policy, null);
        }
        catch (AuthorizationException exception)
        {
            return new AuthorizationResult(AuthorizationPolicy.DenyAll, exception.Message);
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

    private static string? GetNameError(string name, int count)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A role needs a name.";
        }

        if (BuiltInRoles.IsBuiltIn(name))
        {
            return $"The name '{name}' is reserved for a built-in role.";
        }

        if (string.Equals(name, AuthorizationPolicy.SystemRoleName, StringComparison.OrdinalIgnoreCase))
        {
            return $"The name '{name}' is reserved for the system rules.";
        }

        return count > 1 ? $"The name '{name}' is used by more than one role, ignoring case." : null;
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

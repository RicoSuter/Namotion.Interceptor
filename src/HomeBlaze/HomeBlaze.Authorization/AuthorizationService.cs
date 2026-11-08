using System.Reactive.Concurrency;
using HomeBlaze.Authorization.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Authorization;

/// <summary>
/// Compiles the <see cref="AuthorizationPolicy"/> of principals from the built-in roles, the custom roles of the single
/// <see cref="Authorization"/> subject in the graph, the <see cref="SystemRules"/> and <see cref="AuthorizationActions.All"/>,
/// and keeps <see cref="Authorization.Status"/>, <see cref="Role.Status"/>, <see cref="Role.Warning"/> and
/// <see cref="Role.UsedByAgents"/> current.
/// </summary>
/// <remarks>
/// Thread-safe. A policy, once returned, never changes: a change of an <see cref="Authorization"/> or a <see cref="Role"/>
/// gives the next call a newly compiled one. Statuses are updated
/// in the background after such a change.
/// </remarks>
public sealed class AuthorizationService : IDisposable
{
    /// <summary>The status of every <see cref="Authorization"/> and of their roles while several exist.</summary>
    internal const string SeveralAuthorizationsStatus = AuthorizationSnapshot.SeveralAuthorizationsStatus;

    private readonly ISubjectRegistry _registry;
    private readonly PathProviderBase _pathProvider;
    private readonly ILogger<AuthorizationService>? _logger;
    private readonly IDisposable _subscription;
    private readonly PrincipalRoleNames _principals = new();
    private readonly PathPatterns _pathPatterns;

    private readonly Lock _rebuildLock = new();

    // Incremented on every change of an Authorization or a Role; a snapshot of another version is rebuilt.
    private long _version;

    // Incremented after a new snapshot is published and after a principal changed, see WriteStatuses.
    private long _statusVersion;

    private readonly GraphObserver _graphObserver;

    private AuthorizationSnapshot? _snapshot;

    // The version of the last snapshot that read the roles, 0 before the first; guarded by _rebuildLock.
    private long _builtVersion;

    private int _isRefreshScheduled;
    private volatile bool _isDisposed;

    /// <summary>
    /// Creates the service and starts observing the graph of <paramref name="context"/>.
    /// </summary>
    /// <param name="context">
    /// The context of the graph. Its registry finds the <see cref="Authorization"/> subjects; the service registers a
    /// lifecycle handler in it and observes its property changes.
    /// </param>
    /// <param name="pathProvider">
    /// The provider that path patterns in rules are parsed with and matched against. It must be the provider of the tools
    /// that show the graph to the principals, so that a pattern matches the paths a principal sees; it is given here
    /// because those tools are registered by another project, which supplies it.
    /// </param>
    /// <param name="logger">Logs a failed background update of the statuses.</param>
    public AuthorizationService(
        IInterceptorSubjectContext context,
        PathProviderBase pathProvider,
        ILogger<AuthorizationService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pathProvider);

        _registry = context.GetService<ISubjectRegistry>();
        _pathProvider = pathProvider;
        _logger = logger;

        _pathPatterns = new PathPatterns(_registry, pathProvider);
        context.AddService(_pathPatterns);
        _graphObserver = new GraphObserver(Invalidate);
        context.AddService(_graphObserver);
        _subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);

        Invalidate();
    }

    /// <summary>
    /// Changes after every change of the graph that can change whether a subject is shown, see
    /// <see cref="AuthorizationPolicy.IsVisible(RegisteredSubject)"/>: a subject attached or detached, a reference to a
    /// subject added or removed, a kept subject moved to another key or position, a property or attribute added at run
    /// time, and a value of metadata, which decides whether the paths spell an attribute of metadata. A decision about
    /// subjects made after reading it stays valid while it is unchanged; read it before the graph.
    /// </summary>
    public long GraphVersion => _graphObserver.GraphVersion;

    /// <summary>
    /// Raised on the thread pool after the roles were compiled again because an <see cref="Authorization"/> or a
    /// <see cref="Role"/> changed, so that <see cref="GetPolicy(IReadOnlyCollection{string})"/> may now give other results for the same names, for
    /// example to report that a role became known or unknown. Handlers must not throw; an exception is logged and skips the
    /// handlers after it.
    /// </summary>
    public event Action? PoliciesChanged;

    /// <summary>
    /// Gets the policy of a principal holding <paramref name="roleNames"/>, compiled with the built-in roles, the custom
    /// roles of the single <see cref="Authorization"/> subject, the system rules of agents and MCP clients (<see cref="PrincipalKind.NonHuman"/>) and the known actions. Results are cached
    /// per distinct set of names, ignoring order, until an <see cref="Authorization"/> or a <see cref="Role"/>
    /// changes.
    /// </summary>
    /// <returns>
    /// The policy and no error, or <see cref="AuthorizationPolicy.DenyAll"/> and the reason when no name is given, a name is
    /// empty or unknown, a role does not compile, a named custom role cannot be used because of its name, or a custom role is
    /// named while several <see cref="Authorization"/> subjects exist.
    /// </returns>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames) => GetPolicy(roleNames, PrincipalKind.NonHuman);

    /// <summary>
    /// Gets the policy of a principal of <paramref name="kind"/> holding <paramref name="roleNames"/>, compiled with the
    /// system rules of that kind, see <see cref="SystemRules.For"/>; otherwise as <see cref="GetPolicy(IReadOnlyCollection{string})"/>.
    /// </summary>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames, PrincipalKind kind)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        return GetSnapshot().GetPolicy(roleNames, kind);
    }

    /// <summary>
    /// Gets the policy of a principal holding only <paramref name="editedRole"/>, compiled like <see cref="GetPolicy(IReadOnlyCollection{string})"/>, with the system rules of
    /// agents and MCP clients (<see cref="PrincipalKind.NonHuman"/>), and with
    /// <paramref name="editedRole"/> in place of the saved custom role of the same name, or added when none
    /// is saved. Neither policies nor statuses change.
    /// </summary>
    /// <returns>
    /// The policy and no error, or <see cref="AuthorizationPolicy.DenyAll"/> and the status the role would have once saved:
    /// why its name cannot be used, why custom roles are unavailable, or why it does not compile.
    /// </returns>
    public AuthorizationResult PreviewPolicy(AuthorizationRole editedRole) => PreviewPolicy(editedRole, PrincipalKind.NonHuman);

    /// <summary>As <see cref="PreviewPolicy(AuthorizationRole)"/>, with the system rules of <paramref name="kind"/>.</summary>
    public AuthorizationResult PreviewPolicy(AuthorizationRole editedRole, PrincipalKind kind)
    {
        ArgumentNullException.ThrowIfNull(editedRole);

        return GetSnapshot().Preview(editedRole, kind);
    }

    /// <summary>
    /// Gets why a path pattern of a rule is invalid, checked as when a role is compiled, or null when it is valid.
    /// </summary>
    public string? ValidatePathPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        return _pathPatterns.Validate(pattern);
    }

    /// <summary>
    /// Gets why a valid path pattern without a wildcard likely does not do what it says, or null: it names a subject that
    /// is an entry of a folder or collection, which has no member of its own, so the pattern matches nothing. The same
    /// check gives <see cref="Role.Warning"/>; it reads the current graph, so the answer changes when the graph does.
    /// </summary>
    public string? GetPathPatternWarning(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        return pattern.Contains('*') || ValidatePathPattern(pattern) is not null ? null : _pathPatterns.GetWarning(pattern);
    }

    /// <summary>
    /// Gets why a valid path pattern of a deny rule, without a wildcard, likely denies less than it says, or null: it names
    /// a property that holds subjects, such as a reference to a device, so it denies that property alone, and the members
    /// of the subjects below it keep their access when reached by their own paths. It reads the current graph, so the
    /// answer changes when the graph does.
    /// </summary>
    public string? GetDenyPatternWarning(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        return pattern.Contains('*') || ValidatePathPattern(pattern) is not null ? null : _pathPatterns.GetDenyWarning(pattern);
    }

    /// <summary>
    /// Gets whether a deny rule matches at least one member of the known subjects for an action that applies to it, as
    /// <see cref="DenyRuleMatcher"/> tells, or null when the rule does not compile. A rule that matches nothing denies
    /// nothing. The answer is cached per rule, compared by value, until the graph changes.
    /// </summary>
    public bool? DenyRuleMatchesAnyMember(PermissionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return _pathPatterns.DenyRuleMatchesAnyMember(rule);
    }

    /// <summary>
    /// The provider that path patterns in rules are parsed with and matched against, so that a user interface can show
    /// the paths of members as rules match them.
    /// </summary>
    public PathProviderBase PathProvider => _pathProvider;

    /// <summary>
    /// Gets why <paramref name="name"/> cannot name a custom role, or null: it breaks the <see cref="NameRules"/> or is the
    /// name of a built-in role. Uniqueness among the custom roles is not checked.
    /// </summary>
    public static string? GetRoleNameError(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return AuthorizationSnapshot.GetNameError(name, count: 1);
    }

    /// <summary>
    /// Gets the names of the built-in roles, followed by those of the custom roles that names resolve to, ordered by name.
    /// A custom role whose rules do not compile is listed; its <see cref="Role.Status"/> and <see cref="GetPolicy(IReadOnlyCollection{string})"/> report the error.
    /// The same instance is returned until the roles are compiled again, so that a caller can tell whether they were.
    /// </summary>
    public IReadOnlyList<string> GetRoleNames() => GetSnapshot().RoleNames;

    /// <summary>
    /// Gets the custom roles that exist but cannot be used, by name, with the reason: a name that breaks the <see cref="NameRules"/> or is used by more than one role, see
    /// <see cref="GetRoleNameError"/>, or any custom role while several <see cref="Authorization"/>
    /// subjects exist. A custom role named like a built-in role is not listed, as the name means the built-in role.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetUnavailableRoles() => GetSnapshot().UnavailableRoles;

    /// <summary>
    /// Records the role names of a principal, such as an agent, for <see cref="Role.UsedByAgents"/>, which counts the
    /// principals naming a role. The principal is compared by reference; <c>null</c> names remove it.
    /// </summary>
    public void SetPrincipalRoles(object principal, IReadOnlyCollection<string>? roleNames)
    {
        ArgumentNullException.ThrowIfNull(principal);

        _principals.Set(principal, roleNames);
        Interlocked.Increment(ref _statusVersion);
        GetSnapshot(out var hasWrittenStatuses);
        if (!hasWrittenStatuses)
        {
            WriteStatuses();
        }
    }

    /// <summary>
    /// Gets the role names of every principal recorded with <see cref="SetPrincipalRoles"/>, labeled with the
    /// principal's title, or else its type name.
    /// </summary>
    public IReadOnlyList<PrincipalRoleSet> GetPrincipalRoleSets() => _principals.GetRoleSets();

    /// <summary>
    /// Stops observing the graph. Policies already returned stay valid.
    /// </summary>
    public void Dispose()
    {
        _isDisposed = true;
        _subscription.Dispose();
        _graphObserver.Detach();
    }

    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        _graphObserver.OnPropertyChanged(change);
        var name = change.Property.Name;

        // The statuses this service writes are left out, so that writing them does not start another rebuild, and so are
        // the users, which no policy reads.
        var isRoleChange = change.Property.Subject switch
        {
            Role => name is not (nameof(Role.Status) or nameof(Role.Warning) or nameof(Role.UsedByAgents)),
            Authorization => name is not (nameof(Authorization.Status) or nameof(Authorization.Users)),
            _ => false
        };

        if (isRoleChange)
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        if (_isDisposed)
        {
            return;
        }

        Interlocked.Increment(ref _version);
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (!_isDisposed && Interlocked.Exchange(ref _isRefreshScheduled, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static service => service.Refresh(), this, preferLocal: false);
        }
    }

    private void Refresh()
    {
        if (_isDisposed)
        {
            return;
        }

        // Cleared first, so that a change during the rebuild schedules another one.
        Volatile.Write(ref _isRefreshScheduled, 0);
        try
        {
            GetSnapshot();
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Could not update the statuses of the authorization and its roles.");
        }
    }

    internal AuthorizationSnapshot GetSnapshot() => GetSnapshot(out _);

    // hasWrittenStatuses tells whether this call built a snapshot, and so wrote the statuses after publishing it.
    private AuthorizationSnapshot GetSnapshot(out bool hasWrittenStatuses)
    {
        hasWrittenStatuses = false;
        var version = Volatile.Read(ref _version);
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && snapshot.Version == version)
        {
            return snapshot;
        }

        lock (_rebuildLock)
        {
            version = Volatile.Read(ref _version);
            snapshot = _snapshot;
            if (snapshot is not null && snapshot.Version == version)
            {
                return snapshot;
            }

            // Built from the version read before the graph, so a change while building leaves it stale.
            snapshot = BuildSnapshot(version);
            Volatile.Write(ref _snapshot, snapshot);
            Interlocked.Increment(ref _statusVersion);
            if (snapshot.Version == version)
            {
                if (_builtVersion != 0 && _builtVersion != version)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static service => service.RaisePoliciesChanged(), this, preferLocal: false);
                }

                _builtVersion = version;
            }
        }

        // Outside the lock: a write notifies observers synchronously, which must not wait for a rebuild.
        WriteStatuses();
        hasWrittenStatuses = true;
        return snapshot;
    }

    // The caller holds _rebuildLock. The graph or a role list can change while it is read, by an editor or a reload, and
    // its enumeration then throws. Read once more, and when that fails as well, the custom roles are unavailable until the
    // next call, which reads them again because the snapshot is stale.
    private AuthorizationSnapshot BuildSnapshot(long version)
    {
        try
        {
            return AuthorizationSnapshot.Build(version, _registry.KnownSubjects, _pathProvider);
        }
        catch (InvalidOperationException)
        {
            // Changed while it was read; read again.
        }

        try
        {
            return AuthorizationSnapshot.Build(version, _registry.KnownSubjects, _pathProvider);
        }
        catch (InvalidOperationException exception)
        {
            _logger?.LogWarning(exception, "The roles changed while they were read; custom roles are unavailable until they are read again.");
            return AuthorizationSnapshot.CreateUnreadable(_pathProvider);
        }
    }

    private void RaisePoliciesChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            PoliciesChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "A handler of changed authorization policies failed.");
        }
    }

    // Repeats until no snapshot was published and no principal changed while writing, so concurrent writers end with
    // the statuses of the latest snapshot and principals.
    private void WriteStatuses()
    {
        long statusVersion;
        do
        {
            statusVersion = Volatile.Read(ref _statusVersion);
            var snapshot = Volatile.Read(ref _snapshot)!;
            var counts = _principals.CountPerName();

            foreach (var (authorization, status) in snapshot.AuthorizationStatuses)
            {
                authorization.Status = status;
            }

            foreach (var (role, name, status, warning) in snapshot.RoleStatuses)
            {
                role.Status = status;
                role.Warning = warning;
                role.UsedByAgents = counts.GetValueOrDefault(name);
            }
        }
        while (statusVersion != Volatile.Read(ref _statusVersion));
    }
}

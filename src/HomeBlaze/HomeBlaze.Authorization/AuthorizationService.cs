using System.Reactive.Concurrency;
using HomeBlaze.Authorization.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Authorization;

/// <summary>
/// Compiles the <see cref="AuthorizationPolicy"/> of principals from the built-in roles, the custom roles of the single
/// <see cref="Authorization"/> subject in the graph, the <see cref="SystemRules"/> and <see cref="AuthorizationActions.All"/>,
/// and keeps <see cref="Authorization.Status"/>, <see cref="Role.Status"/>, <see cref="Role.Warning"/> and
/// <see cref="Role.UsedByAgents"/> current.
/// </summary>
/// <remarks>
/// Thread-safe. A policy, once returned, never changes: a change of an <see cref="Authorization"/> or a <see cref="Role"/>
/// gives the next call a newly compiled one, unless an update is open, see <see cref="BeginUpdate"/>. Statuses are updated
/// in the background after such a change.
/// </remarks>
public sealed class AuthorizationService : IDisposable
{
    /// <summary>The status of every <see cref="Authorization"/> and of their roles while several exist.</summary>
    internal const string SeveralAuthorizationsStatus =
        "Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.";

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

    private AuthorizationSnapshot? _snapshot;

    // The version of the last snapshot that read the roles, 0 before the first; guarded by _rebuildLock.
    private long _builtVersion;

    // The number of open updates, see BeginUpdate; changed under _rebuildLock.
    private int _updateCount;
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
        context.AddService(new GraphObserver(this));
        _subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);

        Invalidate();
    }

    /// <summary>
    /// Raised on the thread pool after the roles were compiled again because an <see cref="Authorization"/> or a
    /// <see cref="Role"/> changed, so that <see cref="GetPolicy"/> may now give other results for the same names, for
    /// example to report that a role became known or unknown. Handlers must not throw; an exception is logged and skips the
    /// handlers after it.
    /// </summary>
    public event Action? PoliciesChanged;

    /// <summary>
    /// Gets the policy of a principal holding <paramref name="roleNames"/>, compiled with the built-in roles, the custom
    /// roles of the single <see cref="Authorization"/> subject, the system rules and the known actions. Results are cached
    /// per distinct set of names, ignoring case and order, until an <see cref="Authorization"/> or a <see cref="Role"/>
    /// changes.
    /// </summary>
    /// <returns>
    /// The policy and no error, or <see cref="AuthorizationPolicy.DenyAll"/> and the reason when no name is given, a name is
    /// empty or unknown, a role does not compile, a named custom role cannot be used because of its name, or a custom role is
    /// named while several <see cref="Authorization"/> subjects exist.
    /// </returns>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        return GetSnapshot().GetPolicy(roleNames);
    }

    /// <summary>
    /// Gets the policy of a principal holding only <paramref name="editedRole"/>, compiled like <see cref="GetPolicy"/> with
    /// <paramref name="editedRole"/> in place of the saved custom role of the same name, ignoring case, or added when none
    /// is saved. The result is not cached, and neither policies nor statuses change.
    /// </summary>
    /// <returns>
    /// The policy and no error, or <see cref="AuthorizationPolicy.DenyAll"/> and the status the role would have once saved:
    /// why its name cannot be used, why custom roles are unavailable, or why it does not compile.
    /// </returns>
    public AuthorizationResult PreviewPolicy(AuthorizationRole editedRole)
    {
        ArgumentNullException.ThrowIfNull(editedRole);

        return GetSnapshot().Preview(editedRole);
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
    /// The provider that path patterns in rules are parsed with and matched against, so that a user interface can show
    /// the paths of members as rules match them.
    /// </summary>
    public PathProviderBase PathProvider => _pathProvider;

    /// <summary>
    /// Gets why <paramref name="name"/> cannot name a custom role, or null: it is blank, is the name of a built-in role or
    /// of the system rules, ignoring case, or contains <c>/</c>, <c>[</c> or <c>]</c>, which would break the path of the role.
    /// Uniqueness among the custom roles is not checked.
    /// </summary>
    public static string? GetRoleNameError(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return AuthorizationSnapshot.GetNameError(name, count: 1);
    }

    /// <summary>
    /// Gets the names of the built-in roles, followed by those of the custom roles that names resolve to, ordered by name.
    /// A custom role whose rules do not compile is listed; its <see cref="Role.Status"/> and <see cref="GetPolicy"/> report the error.
    /// </summary>
    public IReadOnlyList<string> GetRoleNames() => GetSnapshot().RoleNames;

    /// <summary>
    /// Gets the custom roles that exist but cannot be used, by name ignoring case, with the reason: a name that is blank,
    /// used by more than one role, reserved for the system rules or contains a path character, see
    /// <see cref="GetRoleNameError"/>, or any custom role while several <see cref="Authorization"/>
    /// subjects exist. A custom role named like a built-in role is not listed, as the name means the built-in role.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetUnavailableRoles() => GetSnapshot().UnavailableRoles;

    /// <summary>
    /// Records the role names of a principal, such as an agent, for <see cref="Role.UsedByAgents"/>, which counts the
    /// principals naming a role, ignoring case. The principal is compared by reference; <c>null</c> names remove it.
    /// </summary>
    public void SetPrincipalRoles(object principal, IReadOnlyCollection<string>? roleNames)
    {
        ArgumentNullException.ThrowIfNull(principal);

        _principals.Set(principal, roleNames);
        Interlocked.Increment(ref _statusVersion);
        GetSnapshot();
        WriteStatuses();
    }

    /// <summary>
    /// Gets the number of principals whose role names, recorded with <see cref="SetPrincipalRoles"/>, include
    /// <paramref name="roleName"/>, ignoring case, whether or not a role of that name exists.
    /// </summary>
    public int GetPrincipalCount(string roleName)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        return _principals.Count(roleName);
    }

    /// <summary>
    /// Starts an update of several roles or of the <see cref="Authorization"/> that takes effect as a whole. Until the
    /// returned scope and every other open one are disposed, <see cref="GetPolicy"/>, <see cref="PreviewPolicy"/>,
    /// <see cref="GetRoleNames"/> and <see cref="GetUnavailableRoles"/> keep giving the results compiled before the update,
    /// so that none sees a part of it. Disposing the last scope compiles the roles again in the background.
    /// </summary>
    public IDisposable BeginUpdate()
    {
        lock (_rebuildLock)
        {
            // Taking the lock also waits for a rebuild that reads the roles, which then sees none of the update.
            _updateCount++;
        }

        return new UpdateScope(this);
    }

    /// <summary>
    /// Stops observing property changes. Policies already returned stay valid.
    /// </summary>
    public void Dispose()
    {
        _isDisposed = true;
        _subscription.Dispose();
    }

    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        // The statuses this service writes are left out, so that writing them does not start another rebuild.
        var name = change.Property.Name;
        var isRoleChange = change.Property.Subject switch
        {
            Role => name is not (nameof(Role.Status) or nameof(Role.Warning) or nameof(Role.UsedByAgents)),
            Authorization => name is not nameof(Authorization.Status),
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

    private void EndUpdate()
    {
        bool isLastUpdate;
        lock (_rebuildLock)
        {
            isLastUpdate = --_updateCount == 0;
        }

        if (isLastUpdate)
        {
            ScheduleRefresh();
        }
    }

    private void Refresh()
    {
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

    private AuthorizationSnapshot GetSnapshot()
    {
        var version = Volatile.Read(ref _version);
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && (snapshot.Version == version || Volatile.Read(ref _updateCount) > 0))
        {
            return snapshot;
        }

        lock (_rebuildLock)
        {
            version = Volatile.Read(ref _version);
            snapshot = _snapshot;
            if (snapshot is not null && (snapshot.Version == version || _updateCount > 0))
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

    private sealed class UpdateScope(AuthorizationService service) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                service.EndUpdate();
            }
        }
    }

    // After the registry, so that a rebuild started by a change finds the subject among the known subjects, or no longer.
    [RunsAfter(typeof(SubjectRegistry))]
    private sealed class GraphObserver(AuthorizationService service) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.Subject is Authorization or Role)
            {
                service.Invalidate();
            }
        }
    }
}

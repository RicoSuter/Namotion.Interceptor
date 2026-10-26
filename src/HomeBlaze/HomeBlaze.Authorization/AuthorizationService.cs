using System.Collections.Concurrent;
using System.Collections.Immutable;
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
/// and keeps <see cref="Authorization.Status"/>, <see cref="Role.Status"/> and <see cref="Role.UsedByAgents"/> current.
/// </summary>
/// <remarks>
/// Thread-safe. A policy, once returned, never changes: a change of an <see cref="Authorization"/> or a <see cref="Role"/>
/// gives the next call a newly compiled one. Statuses are updated in the background after such a change.
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
    private readonly ConcurrentDictionary<object, ImmutableArray<string>> _principals = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _rebuildLock = new();

    // Incremented on every change of an Authorization or a Role; a snapshot of another version is rebuilt.
    private long _version;

    // Incremented after a new snapshot is published and after a principal changed, see WriteStatuses.
    private long _statusVersion;

    private AuthorizationSnapshot? _snapshot;
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

        context.AddService(new GraphObserver(this));
        _subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);

        Invalidate();
    }

    /// <summary>
    /// Gets the policy of a principal holding <paramref name="roleNames"/>, compiled with the built-in roles, the custom
    /// roles of the single <see cref="Authorization"/> subject, the system rules and the known actions. Results are cached
    /// per distinct set of names, ignoring case and order, until an <see cref="Authorization"/> or a <see cref="Role"/>
    /// changes.
    /// </summary>
    /// <returns>
    /// The policy and no error, or <see cref="AuthorizationPolicy.DenyAll"/> and the reason when a name is empty or
    /// unknown, a role does not compile, a named custom role cannot be used because of its name, or a custom role is
    /// named while several <see cref="Authorization"/> subjects exist.
    /// </returns>
    public AuthorizationResult GetPolicy(IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        return GetSnapshot().GetPolicy(roleNames);
    }

    /// <summary>
    /// Gets the names of the built-in roles, followed by those of the custom roles that names resolve to, ordered by name.
    /// A custom role whose rules do not compile is listed; its <see cref="Role.Status"/> and <see cref="GetPolicy"/> report the error.
    /// </summary>
    public IReadOnlyList<string> GetRoleNames() => GetSnapshot().RoleNames;

    /// <summary>
    /// Records the role names of a principal, such as an agent, for <see cref="Role.UsedByAgents"/>, which counts the
    /// principals naming a role, ignoring case. The principal is compared by reference; <c>null</c> names remove it.
    /// </summary>
    public void SetPrincipalRoles(object principal, IReadOnlyCollection<string>? roleNames)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (roleNames is null)
        {
            _principals.TryRemove(principal, out _);
        }
        else
        {
            _principals[principal] = [.. roleNames];
        }

        Interlocked.Increment(ref _statusVersion);
        GetSnapshot();
        WriteStatuses();
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
            Role => name is not (nameof(Role.Status) or nameof(Role.UsedByAgents)),
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
        if (Interlocked.Exchange(ref _isRefreshScheduled, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static service => service.Refresh(), this, preferLocal: false);
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
            snapshot = AuthorizationSnapshot.Build(version, _registry.KnownSubjects.Keys, _pathProvider);
            Volatile.Write(ref _snapshot, snapshot);
            Interlocked.Increment(ref _statusVersion);
        }

        // Outside the lock: a write notifies observers synchronously, which must not wait for a rebuild.
        WriteStatuses();
        return snapshot;
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
            var counts = CountPrincipalsPerRoleName();

            foreach (var (authorization, status) in snapshot.AuthorizationStatuses)
            {
                authorization.Status = status;
            }

            foreach (var (role, name, status) in snapshot.RoleStatuses)
            {
                role.Status = status;
                role.UsedByAgents = counts.GetValueOrDefault(name);
            }
        }
        while (statusVersion != Volatile.Read(ref _statusVersion));
    }

    private Dictionary<string, int> CountPrincipalsPerRoleName()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var namesOfPrincipal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, roleNames) in _principals)
        {
            namesOfPrincipal.Clear();
            foreach (var name in roleNames)
            {
                if (name is not null && namesOfPrincipal.Add(name))
                {
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
            }
        }

        return counts;
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

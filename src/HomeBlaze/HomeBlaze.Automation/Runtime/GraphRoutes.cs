using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph routes of the paths that the worker of an automation resolves ahead of time: resolves them with the resolver
/// and records their routes with <see cref="GraphRouteWalker"/>, and builds the union that the worker publishes, so that a
/// graph change reaches the automation only when it can change one of them. A resolution whose walk found something else
/// than the resolver, or failed, has an unbounded route. On a graph change, only the paths whose routes have a changed
/// property are resolved again, see <see cref="NeedsResolving"/>. Used by the worker only.
/// </summary>
/// <remarks>
/// A change that commits while paths are resolved is not routed by a route published only afterwards. The structure
/// version of the registry, read before the first resolution and again after the publication, tells whether such a change
/// may have happened, so that the worker resolves the paths again.
/// </remarks>
internal sealed class GraphRoutes
{
    private readonly GraphRouteWalker _walker;
    private readonly Func<long> _getStructureVersion;
    private readonly AutomationChangeReceiver? _changes;
    private readonly GraphRouteBuilder _builder = new();
    private readonly HashSet<PropertyReference> _union = [];
    private readonly HashSet<PropertyReference> _changed = [];
    private bool _isFullChange = true;
    private bool _isUnionUnbounded;
    private bool _hasResolved;
    private long _resolvedSince;

    // Unbounded until the first publication, as the dispatcher registers a listener.
    private FrozenSet<PropertyReference> _published = FrozenSet<PropertyReference>.Empty;
    private bool _isPublishedUnbounded = true;

    /// <summary>
    /// Creates the routes of paths that <paramref name="resolver"/> resolves for the rules of <paramref name="automation"/>,
    /// or of no automation, in a graph whose registry has the structure version that <paramref name="getStructureVersion"/>
    /// reads, or a negative number without a registry; published to <paramref name="changes"/>, or nowhere without it.
    /// </summary>
    public GraphRoutes(SubjectPathResolver resolver, IInterceptorSubject? automation, Func<long> getStructureVersion, AutomationChangeReceiver? changes)
    {
        Resolver = resolver;
        _walker = new GraphRouteWalker(resolver, automation, getStructureVersion);
        _getStructureVersion = getStructureVersion;
        _changes = changes;
    }

    /// <summary>Creates the routes of the paths of the automation, published to <paramref name="changes"/>.</summary>
    public static GraphRoutes For(Automation automation, AutomationChangeReceiver changes)
    {
        var registry = ((IInterceptorSubject)automation).Context.TryGetService<ISubjectRegistry>();
        return new GraphRoutes(automation.PathResolver, automation, () => registry?.StructureVersion ?? -1, changes);
    }

    public SubjectPathResolver Resolver { get; }

    /// <summary>The route of the canonical path of the automation, which tells when it moved.</summary>
    public GraphRoute Location { get; private set; } = GraphRoute.Empty;

    /// <summary>The structure version that the route of the location was recorded at, see <see cref="GraphRouteBuilder.ResolvedAt"/>.</summary>
    public long LocationVersion { get; private set; } = -1;

    // White-box test hook: runs on the worker before a publication, where a test commits a change after the paths were
    // resolved and before the route that has its property is published.
    internal Action? BeforePublication { get; set; }

    /// <summary>Starts the route of a resolution, which <see cref="GraphRouteBuilder.ToRoute"/> completes.</summary>
    public GraphRouteBuilder BeginRoute()
    {
        var version = _getStructureVersion();
        if (!_hasResolved)
        {
            _hasResolved = true;
            _resolvedSince = version;
        }

        _builder.Clear(version);
        return _builder;
    }

    /// <summary>
    /// Takes the properties of the graph changes queued since the last graph change that the worker took, or that every
    /// path may be affected; without a receiver, every path may be.
    /// </summary>
    public void TakeGraphChanges()
    {
        _changed.Clear();
        _isFullChange = _changes?.TakeGraphChanges(_changed) ?? true;
    }

    /// <summary>
    /// Whether a path with the route, resolved at the structure version <paramref name="resolvedAt"/>, may resolve
    /// differently after the graph changes taken last: its route has a changed property or is unbounded, or the change
    /// may affect every path, and the structure of the graph changed since it was resolved.
    /// </summary>
    public bool NeedsResolving(GraphRoute route, long resolvedAt)
    {
        if (resolvedAt >= 0 && resolvedAt == _getStructureVersion())
        {
            return false;
        }

        if (_isFullChange || route.IsUnbounded)
        {
            return true;
        }

        foreach (var property in route.Properties)
        {
            if (_changed.Contains(property))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves the property as <see cref="AutomationPaths.ResolveProperty"/> does and adds its route.</summary>
    public RegisteredSubjectProperty? ResolveProperty(string path, AutomationRule rule, GraphRouteBuilder route)
    {
        var property = AutomationPaths.ResolveProperty(Resolver, path, rule);
        try
        {
            if (!_walker.WalkProperty(path, rule, property, route))
            {
                route.MarkUnbounded();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            route.MarkUnbounded();
        }

        return property;
    }

    /// <summary>Resolves the subject as <see cref="AutomationPaths.ResolveSubject"/> does and adds its route.</summary>
    public IInterceptorSubject? ResolveSubject(string path, AutomationRule rule, GraphRouteBuilder route)
    {
        var subject = AutomationPaths.ResolveSubject(Resolver, path, rule);
        try
        {
            if (!ReferenceEquals(_walker.WalkSubject(path, rule, route), subject))
            {
                route.MarkUnbounded();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            route.MarkUnbounded();
        }

        return subject;
    }

    /// <summary>Records the route of the canonical path of the automation, before its path is read.</summary>
    public void RecordLocation(Automation automation)
    {
        var route = BeginRoute();
        try
        {
            _walker.WalkLocation(automation, route);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            route.MarkUnbounded();
        }

        Location = route.ToRoute(Location);
        LocationVersion = route.ResolvedAt;
    }

    /// <summary>
    /// Publishes the union of the routes of the paths that the rules resolved ahead of time and of the location, when a
    /// path was resolved since the last publication and the union changed. A structure change while they were resolved may
    /// not be routed by the union published now, so a graph change is queued then, in the chain of the input that resolved
    /// them.
    /// </summary>
    public void Publish(IReadOnlyList<CompiledRule> rules, int chainDepth)
    {
        if (!_hasResolved)
        {
            return;
        }

        BeforePublication?.Invoke();
        BeginUnion();
        AutomationTriggers.AddRoutes(rules, this);
        if (!TryEndUnion(out var route, out var isUnbounded) || _changes is null)
        {
            return;
        }

        _changes.PublishRoute(route, isUnbounded);
        if (HasStructureChangedSinceResolving())
        {
            _changes.RequestGraphChange(chainDepth);
        }
    }

    /// <summary>Adds a route to the union that <see cref="Publish"/> builds.</summary>
    public void Add(GraphRoute route)
    {
        _isUnionUnbounded |= route.IsUnbounded;
        foreach (var property in route.Properties)
        {
            _union.Add(property);
        }
    }

    private void BeginUnion()
    {
        _union.Clear();
        _isUnionUnbounded = false;
        Add(Location);
    }

    // Gets whether the union differs from the last one published, with the route to publish and whether it is unbounded.
    // Allocates only for a changed route.
    private bool TryEndUnion(out FrozenSet<PropertyReference> route, out bool isUnbounded)
    {
        _hasResolved = false;
        isUnbounded = _isUnionUnbounded;
        if (_isPublishedUnbounded == isUnbounded && _published.Count == _union.Count && IsSubsetOf(_union, _published))
        {
            route = _published;
            return false;
        }

        _published = _union.Count == 0 ? FrozenSet<PropertyReference>.Empty : _union.ToFrozenSet();
        _isPublishedUnbounded = isUnbounded;
        route = _published;
        return true;
    }

    // Whether the structure of the graph may have changed since the first resolution before the publication, read after
    // the publication.
    private bool HasStructureChangedSinceResolving()
    {
        var version = _getStructureVersion();
        return version >= 0 && (version != _resolvedSince || version % 2 != 0);
    }

    private static bool IsSubsetOf(HashSet<PropertyReference> properties, FrozenSet<PropertyReference> set)
    {
        foreach (var property in properties)
        {
            if (!set.Contains(property))
            {
                return false;
            }
        }

        return true;
    }
}

using System.Collections.Frozen;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph route of an automation: the union of the routes that the resolver records while the worker resolves the
/// paths of the active rules and the canonical path of the automation, published so that a graph change reaches the
/// automation only when it can change one of them. The paths of the rules are resolved all at once, into one route that
/// replaces the one recorded before, so that the route of a path that is no longer resolved leaves the union. Used by the
/// worker only.
/// </summary>
/// <remarks>
/// The structure version of the registry, which is odd while a change is in progress, is read before the first resolution
/// since the last publication and again after the publication. The paths are those of the current structure only when
/// both reads return the same even number and the location was resolved at it as well, in this pass or in an earlier one
/// at the same number: then a graph change finds nothing to resolve while the version stays that number, see
/// <see cref="NeedsResolving"/>. A change committed while the paths were resolved, which the route published only
/// afterwards may not have routed, queues a graph change.
/// </remarks>
internal sealed class GraphRoutes
{
    private readonly Func<long> _getStructureVersion;
    private readonly AutomationChangeReceiver? _changes;
    private readonly GraphRouteBuilder _rules;
    private readonly GraphRouteBuilder _location;
    private readonly HashSet<PropertyReference> _union = [];
    private bool _hasResolved;
    private bool _hasResolvedLocation;
    private long _resolvedSince;
    private long _boundVersion = -1;

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
        _rules = new GraphRouteBuilder(automation);
        _location = new GraphRouteBuilder(automation);
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

    // White-box test hooks: the route of the paths of the rules, and the route of the canonical path of the automation.
    internal GraphRouteBuilder RulesRoute => _rules;

    internal GraphRouteBuilder LocationRoute => _location;

    // White-box test hook: runs on the worker before a publication, where a test commits a change after the paths were
    // resolved and before the route that has its property is published.
    internal Action? BeforePublication { get; set; }

    /// <summary>
    /// Whether the paths or the location may resolve differently than at the last publication: the structure of the graph
    /// changed since they were resolved, a change was in progress then, or there is no registry.
    /// </summary>
    public bool NeedsResolving()
    {
        var version = _getStructureVersion();
        return version < 0 || version != _boundVersion;
    }

    /// <summary>
    /// Starts the resolution of every path of the rules, whose route replaces the one recorded before. Every path is
    /// resolved with the returned recorder before the worker publishes.
    /// </summary>
    public GraphRouteBuilder BeginRoute()
    {
        NoteResolution();
        _rules.Clear();
        return _rules;
    }

    /// <summary>Resolves the canonical path of the automation and records its route, which replaces the one recorded before.</summary>
    public string? ResolveLocation(Automation automation)
    {
        NoteResolution();
        _hasResolvedLocation = true;
        _location.Clear();
        return Resolver.GetPath(automation, PathStyle.Canonical, _location);
    }

    /// <summary>
    /// Publishes the union of the route of the paths of the rules and of the location, when a path was resolved since the
    /// last publication and the union changed. A structure change while they were resolved may not be routed by the union
    /// published now, and the paths may not be those of the structure now, so a graph change is queued then, in the chain
    /// of the input that resolved them.
    /// </summary>
    public void Publish(int chainDepth)
    {
        if (!_hasResolved)
        {
            return;
        }

        BeforePublication?.Invoke();
        if (TryEndUnion(out var route, out var isUnbounded))
        {
            _changes?.PublishRoute(route, isUnbounded);
        }

        BindVersion(chainDepth);
    }

    // Reads the version after the publication, so that a change committed before it is either routed by the route
    // published now or seen here. A pass that resolved the paths of the rules alone leaves the location bound only while
    // the version is the one it was resolved at.
    private void BindVersion(int chainDepth)
    {
        var version = _getStructureVersion();
        var isCurrent = version >= 0 && version == _resolvedSince && version % 2 == 0;
        _boundVersion = isCurrent && (_hasResolvedLocation || _boundVersion == version) ? version : -1;
        _hasResolvedLocation = false;
        if (!isCurrent && version >= 0)
        {
            _changes?.RequestGraphChange(chainDepth);
        }
    }

    // Reads the version before the resolution reads the graph, so that a change that commits during the resolution is
    // seen as one that happened since.
    private void NoteResolution()
    {
        if (!_hasResolved)
        {
            _hasResolved = true;
            _resolvedSince = _getStructureVersion();
        }
    }

    // Gets whether the union differs from the last one published, with the route to publish and whether it is unbounded.
    // Allocates only for a changed route.
    private bool TryEndUnion(out FrozenSet<PropertyReference> route, out bool isUnbounded)
    {
        _hasResolved = false;
        _union.Clear();
        AddToUnion(_location.Properties);
        AddToUnion(_rules.Properties);
        isUnbounded = _location.IsUnbounded || _rules.IsUnbounded;
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

    private void AddToUnion(ReadOnlySpan<PropertyReference> properties)
    {
        foreach (var property in properties)
        {
            _union.Add(property);
        }
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

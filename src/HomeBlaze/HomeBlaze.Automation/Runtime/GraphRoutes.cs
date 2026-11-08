using System.Collections.Frozen;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph routes of the stored resolutions of one automation: records the route of each resolution that the worker
/// stores, see <see cref="GraphRouteSlot"/>, keeps the union of the stored routes and publishes it to the receiver of the
/// automation, so that a graph change reaches the automation only when it can change a stored result. Used by the worker
/// only.
/// </summary>
/// <remarks>
/// A change that commits while the worker resolves is routed by the union published before, which has every property
/// that can change a result stored before. A union that changed is published only after the resolutions, so the
/// structure version of the registry, odd while a change is in progress, is read before the first resolution since the
/// last publication and again after the publication; when it may have moved, a graph change is queued, so that the
/// automation resolves again as it would on the graph change of that commit.
/// </remarks>
internal sealed class GraphRoutes
{
    private readonly GraphRouteBuilder _builder;
    private readonly AutomationChangeReceiver? _changes;
    private readonly Func<long> _getStructureVersion;
    private bool _hasResolved;
    private long _resolvedSince;

    // How many stored routes have each property, and how many are unbounded; a property no stored route has is removed.
    private readonly Dictionary<PropertyReference, int> _counts = [];
    private int _unboundedCount;

    // Set when a property enters or leaves the union or the union becomes bounded or unbounded.
    private bool _hasUnionChanged;

    // Unbounded until the first publication, as the dispatcher registers a listener.
    private FrozenSet<PropertyReference> _published = FrozenSet<PropertyReference>.Empty;
    private bool _isPublishedUnbounded = true;
    private bool _hasPublished;

    /// <summary>
    /// Creates the routes of the resolutions of <paramref name="automation"/>, or of no automation, published to
    /// <paramref name="changes"/>, or nowhere without it, in a graph whose registry has the structure version that
    /// <paramref name="getStructureVersion"/> reads, or a negative number without a registry.
    /// </summary>
    public GraphRoutes(IInterceptorSubject? automation, AutomationChangeReceiver? changes = null, Func<long>? getStructureVersion = null)
    {
        _builder = new GraphRouteBuilder(automation);
        _changes = changes;
        _getStructureVersion = getStructureVersion ?? (() => -1);
    }

    /// <summary>Creates the routes of the resolutions of the automation, published to <paramref name="changes"/>.</summary>
    public static GraphRoutes For(Automation automation, AutomationChangeReceiver changes)
    {
        var registry = ((IInterceptorSubject)automation).Context.TryGetService<ISubjectRegistry>();
        return new GraphRoutes(automation, changes, () => registry?.StructureVersion ?? -1);
    }

    /// <summary>The route of the canonical path of the automation, which names its checkpoint.</summary>
    public GraphRouteSlot Location { get; } = new();

    // White-box test hook: the properties of the union of the stored routes.
    internal IReadOnlyCollection<PropertyReference> Union => _counts.Keys;

    // White-box test hook: whether a stored route is unbounded.
    internal bool IsUnbounded => _unboundedCount > 0;

    // White-box test hook: the number of resolutions whose route was recorded.
    internal int ResolutionCount { get; private set; }

    // White-box test hook: runs on the worker before a changed union is published, where a test commits a change after
    // the resolutions and before the union that has its property is published.
    internal Action? BeforePublication { get; set; }

    /// <summary>Starts a resolution whose result is stored, which records its route with the returned recorder.</summary>
    public ISubjectPathRecorder BeginRoute()
    {
        ResolutionCount++;

        // Read before the resolution reads the graph, so that a change that commits during it is seen as one since.
        if (!_hasResolved)
        {
            _hasResolved = true;
            _resolvedSince = _getStructureVersion();
        }

        _builder.Clear();
        return _builder;
    }

    /// <summary>Stores the route recorded since <see cref="BeginRoute"/> in the slot of the stored result.</summary>
    public void EndRoute(GraphRouteSlot slot) => slot.Store(this, _builder.ToRoute(slot.Route));

    /// <summary>
    /// Publishes the union of the stored routes when it differs from the one published last, and queues a graph change
    /// caused at <paramref name="chainDepth"/> when the structure may have changed since the first resolution after the
    /// last publication; allocates nothing when the union did not change. Returns whether it published.
    /// </summary>
    public bool Publish(int chainDepth)
    {
        var hasResolved = _hasResolved;
        _hasResolved = false;
        if (!_hasUnionChanged)
        {
            return false;
        }

        _hasUnionChanged = false;
        var isUnbounded = _unboundedCount > 0;
        if (IsPublished(isUnbounded))
        {
            return false;
        }

        BeforePublication?.Invoke();
        _published = _counts.Count == 0 ? FrozenSet<PropertyReference>.Empty : _counts.Keys.ToFrozenSet();
        _isPublishedUnbounded = isUnbounded;
        _hasPublished = true;
        _changes?.PublishRoute(_published, isUnbounded);
        if (hasResolved)
        {
            QueueGraphChangeIfStructureMoved(chainDepth);
        }

        return true;
    }

    internal void Add(GraphRoute route)
    {
        foreach (var property in route.Properties)
        {
            var count = _counts.GetValueOrDefault(property);
            _counts[property] = count + 1;
            _hasUnionChanged |= count == 0;
        }

        if (route.IsUnbounded && _unboundedCount++ == 0)
        {
            _hasUnionChanged = true;
        }
    }

    internal void Remove(GraphRoute route)
    {
        foreach (var property in route.Properties)
        {
            var count = _counts[property] - 1;
            if (count == 0)
            {
                _counts.Remove(property);
                _hasUnionChanged = true;
            }
            else
            {
                _counts[property] = count;
            }
        }

        if (route.IsUnbounded && --_unboundedCount == 0)
        {
            _hasUnionChanged = true;
        }
    }

    // The publication fences after its write, and the registry requires a full fence before the second read of the
    // version: a change that commits later is routed by the published union, an earlier one is seen here.
    private void QueueGraphChangeIfStructureMoved(int chainDepth)
    {
        Interlocked.MemoryBarrier();
        var version = _getStructureVersion();
        if (version >= 0 && (version != _resolvedSince || version % 2 != 0))
        {
            _changes?.RequestGraphChange(chainDepth);
        }
    }

    private bool IsPublished(bool isUnbounded)
    {
        if (!_hasPublished || isUnbounded != _isPublishedUnbounded || _counts.Count != _published.Count)
        {
            return false;
        }

        foreach (var property in _counts.Keys)
        {
            if (!_published.Contains(property))
            {
                return false;
            }
        }

        return true;
    }
}

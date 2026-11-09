using System.Collections.Frozen;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph routes of the stored resolutions of one automation: records the route of each resolution that the worker
/// stores, see <see cref="GraphRouteSlot"/>, keeps the union of the stored routes and publishes it to the receiver of the
/// automation, so that a graph change reaches the automation only when it can change a stored result. Used by the worker
/// only.
/// </summary>
/// <remarks>
/// A change that commits while the worker resolves is routed by the union published before, which may lack a property
/// that a new result depends on. From the first resolution after a publication until the next publication the receiver
/// therefore receives every graph change, as it does without routes.
/// </remarks>
internal sealed class GraphRoutes
{
    private readonly GraphRouteBuilder _builder;
    private readonly AutomationChangeReceiver? _changes;
    private bool _isResolving;

    // How many stored routes have each property, and how many are unbounded; a property no stored route has is removed.
    private readonly Dictionary<PropertyReference, int> _counts = [];
    private int _unboundedCount;

    // Set when a property enters or leaves the union or the union becomes bounded or unbounded.
    private bool _hasUnionChanged;
    private bool _awaitsGraphChange;
    private long _staleVersion = -1;

    // Unbounded until the first publication, as the dispatcher registers a listener.
    private FrozenSet<PropertyReference> _published = FrozenSet<PropertyReference>.Empty;
    private bool _isPublishedUnbounded = true;
    private bool _hasPublished;

    /// <summary>
    /// Creates the routes of the resolutions of <paramref name="automation"/>, or of no automation, published to
    /// <paramref name="changes"/>, or nowhere without it.
    /// </summary>
    public GraphRoutes(IInterceptorSubject? automation, AutomationChangeReceiver? changes)
    {
        _builder = new GraphRouteBuilder(automation);
        _changes = changes;
    }

    /// <summary>The route of the canonical path of the automation, which names its checkpoint.</summary>
    public GraphRouteSlot Location { get; } = new();

    // White-box test hook: the properties of the union of the stored routes.
    internal IReadOnlyCollection<PropertyReference> Union => _counts.Keys;

    // White-box test hook: whether the union is unbounded.
    internal bool IsUnbounded => _unboundedCount > 0 || _awaitsGraphChange || _staleVersion >= 0;

    // White-box test hook: the number of resolutions whose route was recorded.
    internal int ResolutionCount { get; private set; }

    // White-box test hook: runs on the worker before a changed union is published, where a test commits a change after
    // the resolutions and before the union that has its property is published.
    internal Action? BeforePublication { get; set; }

    /// <summary>Starts a resolution whose result is stored, which records its route with the returned recorder.</summary>
    public ISubjectPathRecorder BeginRoute()
    {
        ResolutionCount++;
        BeginResolving();
        _builder.Clear();
        return _builder;
    }

    /// <summary>
    /// Lets every graph change reach the automation until the next publication, from before the worker reads the graph
    /// for results it stores.
    /// </summary>
    public void BeginResolving()
    {
        if (!_isResolving)
        {
            _isResolving = true;
            _changes?.BeginResolving();
        }
    }

    /// <summary>Stores the route recorded since <see cref="BeginRoute"/> in the slot of the stored result.</summary>
    public void EndRoute(GraphRouteSlot slot) => slot.Store(this, _builder.ToRoute(slot.Route));

    /// <summary>
    /// Sets whether the worker holds results that the next graph change evaluates whatever it changed, such as triggers
    /// that a refresh outside a graph change resolved differently; the union is unbounded while it does.
    /// </summary>
    public void SetAwaitsGraphChange(bool awaitsGraphChange)
    {
        if (awaitsGraphChange != _awaitsGraphChange)
        {
            _awaitsGraphChange = awaitsGraphChange;
            _hasUnionChanged = true;
        }
    }

    /// <summary>
    /// Keeps the union unbounded until the paths are resolved after a graph change at another structure version, since
    /// what they resolve to at <paramref name="version"/> may be out of date: a write that the registry does not count,
    /// such as a second key for a subject that a dictionary holds already, leaves the version and the cached resolutions
    /// as they were, and a change in progress may be half seen. The paths resolve again at the next structural change
    /// anywhere, which then reaches the automation.
    /// </summary>
    public void KeepUnboundedAt(long version)
    {
        if (version > _staleVersion)
        {
            _staleVersion = version;
            _hasUnionChanged = true;
        }
    }

    /// <summary>
    /// Tells that the paths were resolved after a graph change at the structure version, see <see cref="KeepUnboundedAt"/>;
    /// an odd version is a change in progress.
    /// </summary>
    public void OnResolvedAt(long version)
    {
        if (version % 2 != 0)
        {
            KeepUnboundedAt(version);
        }
        else if (_staleVersion >= 0 && version != _staleVersion)
        {
            _staleVersion = -1;
            _hasUnionChanged = true;
        }
    }

    /// <summary>
    /// Publishes the union of the stored routes when it differs from the one published last, and ends the window that
    /// <see cref="BeginResolving"/> began; allocates nothing when the union did not change. Returns whether it published.
    /// </summary>
    public bool Publish()
    {
        if (_changes?.TakeUncountedVersion() is >= 0 and var version)
        {
            KeepUnboundedAt(version);
        }

        var hasPublished = PublishIfChanged();
        if (_isResolving)
        {
            _isResolving = false;
            _changes?.EndResolving();
        }

        return hasPublished;
    }

    private bool PublishIfChanged()
    {
        if (!_hasUnionChanged)
        {
            return false;
        }

        _hasUnionChanged = false;
        var isUnbounded = IsUnbounded;
        var hasPublishedProperties = HasPublishedProperties();
        if (hasPublishedProperties && isUnbounded == _isPublishedUnbounded)
        {
            return false;
        }

        BeforePublication?.Invoke();
        if (!hasPublishedProperties)
        {
            _published = _counts.Count == 0 ? FrozenSet<PropertyReference>.Empty : _counts.Keys.ToFrozenSet();
        }

        _isPublishedUnbounded = isUnbounded;
        _hasPublished = true;
        _changes?.PublishRoute(_published, isUnbounded);
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

    private bool HasPublishedProperties()
    {
        if (!_hasPublished || _counts.Count != _published.Count)
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

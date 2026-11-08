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
internal sealed class GraphRoutes
{
    private readonly GraphRouteBuilder _builder;
    private readonly AutomationChangeReceiver? _changes;

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
    /// <paramref name="changes"/>, or nowhere without it.
    /// </summary>
    public GraphRoutes(IInterceptorSubject? automation, AutomationChangeReceiver? changes = null)
    {
        _builder = new GraphRouteBuilder(automation);
        _changes = changes;
    }

    /// <summary>The route of the canonical path of the automation, which names its checkpoint.</summary>
    public GraphRouteSlot Location { get; } = new();

    // White-box test hook: the properties of the union of the stored routes.
    internal IReadOnlyCollection<PropertyReference> Union => _counts.Keys;

    // White-box test hook: whether a stored route is unbounded.
    internal bool IsUnbounded => _unboundedCount > 0;

    // White-box test hook: the number of resolutions whose route was recorded.
    internal int ResolutionCount { get; private set; }

    /// <summary>Starts a resolution whose result is stored, which records its route with the returned recorder.</summary>
    public ISubjectPathRecorder BeginRoute()
    {
        ResolutionCount++;
        _builder.Clear();
        return _builder;
    }

    /// <summary>Stores the route recorded since <see cref="BeginRoute"/> in the slot of the stored result.</summary>
    public void EndRoute(GraphRouteSlot slot) => slot.Store(this, _builder.ToRoute(slot.Route));

    /// <summary>
    /// Publishes the union of the stored routes when it differs from the one published last; allocates nothing when it
    /// does not. Returns whether it published.
    /// </summary>
    public bool Publish()
    {
        if (!_hasUnionChanged)
        {
            return false;
        }

        _hasUnionChanged = false;
        var isUnbounded = _unboundedCount > 0;
        if (_hasPublished && isUnbounded == _isPublishedUnbounded && _counts.Count == _published.Count && IsPublished(_counts.Keys))
        {
            return false;
        }

        _published = _counts.Count == 0 ? FrozenSet<PropertyReference>.Empty : _counts.Keys.ToFrozenSet();
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

    private bool IsPublished(Dictionary<PropertyReference, int>.KeyCollection properties)
    {
        foreach (var property in properties)
        {
            if (!_published.Contains(property))
            {
                return false;
            }
        }

        return true;
    }
}

using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph routes of the stored resolutions of one automation: records the route of each resolution that the worker
/// stores, see <see cref="GraphRouteSlot"/>, and keeps the union of the stored routes. Used by the worker only.
/// </summary>
internal sealed class GraphRoutes
{
    private readonly GraphRouteBuilder _builder;

    // How many stored routes have each property, and how many are unbounded; a property no stored route has is removed.
    private readonly Dictionary<PropertyReference, int> _counts = [];
    private int _unboundedCount;

    /// <summary>Creates the routes of the resolutions of <paramref name="automation"/>, or of no automation.</summary>
    public GraphRoutes(IInterceptorSubject? automation)
    {
        _builder = new GraphRouteBuilder(automation);
    }

    /// <summary>The route of the canonical path of the automation, which names its checkpoint.</summary>
    public GraphRouteSlot Location { get; } = new();

    // White-box test hook: the properties of the union of the stored routes.
    internal IReadOnlyCollection<PropertyReference> Union => _counts.Keys;

    // White-box test hook: whether a stored route is unbounded.
    internal bool IsUnbounded => _unboundedCount > 0;

    /// <summary>Starts a resolution whose result is stored, which records its route with the returned recorder.</summary>
    public ISubjectPathRecorder BeginRoute()
    {
        _builder.Clear();
        return _builder;
    }

    /// <summary>Stores the route recorded since <see cref="BeginRoute"/> in the slot of the stored result.</summary>
    public void EndRoute(GraphRouteSlot slot) => slot.Store(this, _builder.ToRoute(slot.Route));

    internal void Add(GraphRoute route)
    {
        foreach (var property in route.Properties)
        {
            _counts[property] = _counts.GetValueOrDefault(property) + 1;
        }

        if (route.IsUnbounded)
        {
            _unboundedCount++;
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
            }
            else
            {
                _counts[property] = count;
            }
        }

        if (route.IsUnbounded)
        {
            _unboundedCount--;
        }
    }
}

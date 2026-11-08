namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph route of a stored resolution, next to its result, counted in the <see cref="GraphRoutes"/> of the worker
/// that stored it until it is cleared or moved. Used by the worker only.
/// </summary>
internal sealed class GraphRouteSlot
{
    private GraphRoutes? _routes;

    public GraphRoute Route { get; private set; } = GraphRoute.Empty;

    /// <summary>Stores the route of a resolution, counted in <paramref name="routes"/>; nothing changes for the stored instance.</summary>
    public void Store(GraphRoutes routes, GraphRoute route)
    {
        if (ReferenceEquals(route, Route) && ReferenceEquals(routes, _routes))
        {
            return;
        }

        _routes?.Remove(Route);
        routes.Add(route);
        Route = route;
        _routes = routes;
    }

    /// <summary>Clears the route, for a result that is no longer resolved.</summary>
    public void Clear()
    {
        _routes?.Remove(Route);
        Route = GraphRoute.Empty;
        _routes = null;
    }

    /// <summary>Hands the route to the slot of the result that the stored result moved to, which keeps it counted.</summary>
    public void MoveTo(GraphRouteSlot target)
    {
        target.Clear();
        target.Route = Route;
        target._routes = _routes;
        Route = GraphRoute.Empty;
        _routes = null;
    }
}

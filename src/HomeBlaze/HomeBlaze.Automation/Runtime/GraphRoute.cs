using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The graph route of a resolution: the properties whose change can make it find something else. Those are the properties
/// that hold subjects which it read, and the properties it looked up and did not find or may not read, since a dynamic
/// property or its metadata can be added later. An unbounded route stands for a resolution whose route is not known
/// exactly, which every change of a property that holds subjects may affect besides its properties. Never changed once
/// created.
/// </summary>
internal sealed class GraphRoute
{
    private GraphRoute(PropertyReference[] properties, bool isUnbounded)
    {
        Properties = properties;
        IsUnbounded = isUnbounded;
    }

    /// <summary>The route of a resolution that no change affects, and of one that did not happen.</summary>
    public static GraphRoute Empty { get; } = new([], false);

    /// <summary>The properties of the route, each once.</summary>
    public PropertyReference[] Properties { get; }

    public bool IsUnbounded { get; }

    public static GraphRoute Of(PropertyReference[] properties, bool isUnbounded) =>
        properties.Length == 0 && !isUnbounded ? Empty : new GraphRoute(properties, isUnbounded);
}

using System.Globalization;

namespace Namotion.Matter;

/// <summary>Identifies an event of a node by endpoint number, cluster ID and event ID.</summary>
/// <param name="Endpoint">The endpoint number.</param>
/// <param name="Cluster">The cluster ID.</param>
/// <param name="Event">The event ID.</param>
public readonly record struct EventPath(ushort Endpoint, uint Cluster, uint Event)
{
    /// <summary>Formats the path as <c>endpoint/cluster/event</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{Cluster}/{Event}");
}

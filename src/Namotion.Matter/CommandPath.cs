using System.Globalization;

namespace Namotion.Matter;

/// <summary>Identifies a command of a node by endpoint number, cluster ID and command ID.</summary>
/// <param name="Endpoint">The endpoint number.</param>
/// <param name="Cluster">The cluster ID.</param>
/// <param name="Command">The command ID.</param>
public readonly record struct CommandPath(ushort Endpoint, uint Cluster, uint Command)
{
    /// <summary>Formats the path as <c>endpoint/cluster/command</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{Cluster}/{Command}");
}

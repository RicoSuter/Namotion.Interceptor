namespace Namotion.Matter;

/// <summary>An event of a controller, delivered in the order the controller observed it.</summary>
public abstract record MatterControllerEvent;

/// <summary>A node was commissioned or imported.</summary>
/// <param name="Node">The node.</param>
public sealed record MatterNodeAddedEvent(MatterNode Node) : MatterControllerEvent;

/// <summary>A node's structure, availability or set of attributes changed; the node replaces the previous one.</summary>
/// <param name="Node">The node.</param>
public sealed record MatterNodeUpdatedEvent(MatterNode Node) : MatterControllerEvent;

/// <summary>A node was removed.</summary>
/// <param name="NodeId">The node ID.</param>
public sealed record MatterNodeRemovedEvent(ulong NodeId) : MatterControllerEvent;

/// <summary>An endpoint was added to a node.</summary>
/// <param name="NodeId">The node ID.</param>
/// <param name="Endpoint">The endpoint number.</param>
public sealed record MatterEndpointAddedEvent(ulong NodeId, ushort Endpoint) : MatterControllerEvent;

/// <summary>An endpoint was removed from a node.</summary>
/// <param name="NodeId">The node ID.</param>
/// <param name="Endpoint">The endpoint number.</param>
public sealed record MatterEndpointRemovedEvent(ulong NodeId, ushort Endpoint) : MatterControllerEvent;

/// <summary>An attribute value changed. A null value also means the attribute was removed.</summary>
/// <param name="NodeId">The node ID.</param>
/// <param name="Path">The attribute path.</param>
/// <param name="Value">The new value.</param>
public sealed record MatterAttributeUpdatedEvent(ulong NodeId, AttributePath Path, MatterValue Value) : MatterControllerEvent;

/// <summary>A node reported a Matter event, such as a button press.</summary>
/// <param name="NodeId">The node ID.</param>
/// <param name="Path">The event path.</param>
/// <param name="EventNumber">The event number, increasing per node.</param>
/// <param name="Priority">The event priority: 0 debug, 1 info, 2 critical.</param>
/// <param name="Timestamp">The timestamp in milliseconds, on the clock <paramref name="TimestampType"/> names.</param>
/// <param name="TimestampType">The clock of <paramref name="Timestamp"/>.</param>
/// <param name="Data">The event payload, a struct keyed by field name, or null for an event without fields.</param>
public sealed record MatterNodeEventReceivedEvent(
    ulong NodeId,
    EventPath Path,
    ulong EventNumber,
    byte Priority,
    long Timestamp,
    MatterEventTimestampType TimestampType,
    MatterValue Data) : MatterControllerEvent;

/// <summary>The controller's information changed, for example after credentials were set.</summary>
/// <param name="ServerInfo">The new information.</param>
public sealed record MatterServerInfoUpdatedEvent(MatterServerInfo ServerInfo) : MatterControllerEvent;

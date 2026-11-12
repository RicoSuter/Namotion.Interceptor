using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client.Subscriptions.MonitoredItems;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// A monitored item to create for a property: the property it feeds and the options to create it with.
/// </summary>
internal readonly record struct MonitoredItemRequest(RegisteredSubjectProperty Property, MonitoredItemOptions Options)
{
    public NodeId NodeId => Options.StartNodeId;
}

using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client.Subscriptions.MonitoredItems;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Factory for creating monitored item requests from configuration.
/// Extracted from OpcUaClientConfiguration for single responsibility.
/// </summary>
internal static class MonitoredItemFactory
{
    /// <summary>
    /// Creates a monitored item request for the given property and node ID using the configuration defaults.
    /// Mapper configuration overrides (SamplingInterval, QueueSize, DiscardOldest, DataChangeTrigger, DeadbandType, DeadbandValue)
    /// are applied if present on the property.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="nodeId">The OPC UA node ID to monitor.</param>
    /// <param name="property">The property to associate with the monitored item.</param>
    /// <param name="rootSubject">The root subject used for mapping resolution.</param>
    /// <returns>A request ready to be added to a subscription.</returns>
    public static MonitoredItemRequest Create(OpcUaClientConfiguration configuration, NodeId nodeId, RegisteredSubjectProperty property, IInterceptorSubject rootSubject)
    {
        var mapping = configuration.Mapper.TryGetMapping(property, rootSubject, out var m) ? m : null;

        var options = new MonitoredItemOptions
        {
            StartNodeId = nodeId,
            AttributeId = Opc.Ua.Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,

            // -1 lets the server use the subscription's publishing interval.
            SamplingInterval = TimeSpan.FromMilliseconds(mapping?.SamplingInterval ?? configuration.DefaultSamplingInterval ?? -1),
            QueueSize = mapping?.QueueSize ?? configuration.DefaultQueueSize ?? 0,
            DiscardOldest = mapping?.DiscardOldest ?? configuration.DefaultDiscardOldest ?? true,
            Filter = CreateDataChangeFilter(configuration, mapping)
        };

        return new MonitoredItemRequest(property, options);
    }

    /// <summary>
    /// Creates a DataChangeFilter based on the node configuration and configuration defaults.
    /// Returns null if no filter options are specified (uses OPC UA library defaults).
    /// </summary>
    private static DataChangeFilter? CreateDataChangeFilter(OpcUaClientConfiguration configuration, OpcUaPropertyMapping? mapping)
    {
        // Apply Mapper configuration overrides, then configuration defaults
        var trigger = mapping?.DataChangeTrigger ?? configuration.DefaultDataChangeTrigger;
        var deadbandType = mapping?.DeadbandType ?? configuration.DefaultDeadbandType;
        var deadbandValue = mapping?.DeadbandValue ?? configuration.DefaultDeadbandValue;

        // Only create filter if at least one option is specified
        if (!trigger.HasValue && !deadbandType.HasValue && !deadbandValue.HasValue)
        {
            return null;
        }

        return new DataChangeFilter
        {
            Trigger = trigger ?? DataChangeTrigger.StatusValue,
            DeadbandType = (uint)(deadbandType ?? DeadbandType.None),
            DeadbandValue = deadbandValue ?? 0.0
        };
    }
}

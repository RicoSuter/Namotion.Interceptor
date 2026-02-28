using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Factory for creating MonitoredItem instances from configuration.
/// Extracted from OpcUaClientConfiguration for single responsibility.
/// </summary>
internal static class MonitoredItemFactory
{
    /// <summary>
    /// Creates a MonitoredItem for the given property and node ID using the configuration defaults.
    /// NodeMapper configuration overrides (SamplingInterval, QueueSize, DiscardOldest, DataChangeTrigger, DeadbandType, DeadbandValue)
    /// are applied if present on the property.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="nodeId">The OPC UA node ID to monitor.</param>
    /// <param name="property">The property to associate with the monitored item.</param>
    /// <returns>A configured MonitoredItem ready to be added to a subscription.</returns>
    public static MonitoredItem Create(OpcUaClientConfiguration configuration, NodeId nodeId, RegisteredSubjectProperty property)
    {
        var nodeConfiguration = configuration.NodeMapper.TryGetNodeConfiguration(property);
        var item = new MonitoredItem(configuration.TelemetryContext)
        {
            StartNodeId = nodeId,
            AttributeId = Opc.Ua.Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            Handle = property
        };

        // Apply sampling/queue settings from NodeMapper configuration
        var samplingInterval = nodeConfiguration?.SamplingInterval ?? configuration.DefaultSamplingInterval;
        if (samplingInterval.HasValue)
        {
            item.SamplingInterval = samplingInterval.Value;
        }

        var queueSize = nodeConfiguration?.QueueSize ?? configuration.DefaultQueueSize;
        if (queueSize.HasValue)
        {
            item.QueueSize = queueSize.Value;
        }

        var discardOldest = nodeConfiguration?.DiscardOldest ?? configuration.DefaultDiscardOldest;
        if (discardOldest.HasValue)
        {
            item.DiscardOldest = discardOldest.Value;
        }

        // Apply filter (only if any filter option is specified)
        var filter = CreateDataChangeFilter(configuration, nodeConfiguration);
        if (filter != null)
        {
            item.Filter = filter;
        }

        return item;
    }

    /// <summary>
    /// Creates a DataChangeFilter based on the node configuration and configuration defaults.
    /// Returns null if no filter options are specified (uses OPC UA library defaults).
    /// </summary>
    private static DataChangeFilter? CreateDataChangeFilter(OpcUaClientConfiguration configuration, OpcUaNodeConfiguration? nodeConfiguration)
    {
        // Apply NodeMapper configuration overrides, then configuration defaults
        var trigger = nodeConfiguration?.DataChangeTrigger ?? configuration.DefaultDataChangeTrigger;
        var deadbandType = nodeConfiguration?.DeadbandType ?? configuration.DefaultDeadbandType;
        var deadbandValue = nodeConfiguration?.DeadbandValue ?? configuration.DefaultDeadbandValue;

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

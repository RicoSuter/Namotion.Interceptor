using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Extension methods for <see cref="OpcUaNodeAttribute"/> to handle sentinel value checking.
/// C# attributes don't support nullable value types, so sentinel values are used instead.
/// These extension methods provide nullable-safe access to attribute properties.
/// </summary>
internal static class OpcUaNodeAttributeExtensions
{
    /// <summary>
    /// Gets the sampling interval if explicitly set, or null if using the sentinel value (int.MinValue).
    /// </summary>
    public static int? GetSamplingIntervalOrNull(this OpcUaNodeAttribute? attribute)
        => attribute != null && attribute.SamplingInterval != int.MinValue
            ? attribute.SamplingInterval
            : null;

    /// <summary>
    /// Gets the queue size if explicitly set, or null if using the sentinel value (uint.MaxValue).
    /// </summary>
    public static uint? GetQueueSizeOrNull(this OpcUaNodeAttribute? attribute)
        => attribute != null && attribute.QueueSize != uint.MaxValue
            ? attribute.QueueSize
            : null;

    /// <summary>
    /// Gets the discard oldest setting if explicitly set, or null if using the sentinel value (Unset).
    /// </summary>
    public static bool? GetDiscardOldestOrNull(this OpcUaNodeAttribute? attribute)
        => attribute?.DiscardOldest switch
        {
            DiscardOldestMode.True => true,
            DiscardOldestMode.False => false,
            _ => null
        };

    /// <summary>
    /// Gets the data change trigger if explicitly set, or null if using the sentinel value (-1).
    /// </summary>
    public static DataChangeTrigger? GetDataChangeTriggerOrNull(this OpcUaNodeAttribute? attribute)
        => attribute != null && (int)attribute.DataChangeTrigger != -1
            ? attribute.DataChangeTrigger
            : null;

    /// <summary>
    /// Gets the deadband type if explicitly set, or null if using the sentinel value (-1).
    /// </summary>
    public static DeadbandType? GetDeadbandTypeOrNull(this OpcUaNodeAttribute? attribute)
        => attribute != null && (int)attribute.DeadbandType != -1
            ? attribute.DeadbandType
            : null;

    /// <summary>
    /// Gets the deadband value if explicitly set, or null if using the sentinel value (NaN).
    /// </summary>
    public static double? GetDeadbandValueOrNull(this OpcUaNodeAttribute? attribute)
        => attribute != null && !double.IsNaN(attribute.DeadbandValue)
            ? attribute.DeadbandValue
            : null;
}

internal static class OpcUaPropertyExtensions
{
    public static string? ResolvePropertyName(this RegisteredSubjectProperty property, PathProviderBase pathProvider)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            var propertyName = pathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;

            // TODO: Create property reference node instead of __?
            return attributedProperty.ResolvePropertyName(pathProvider) + "__" + propertyName;
        }

        return pathProvider.TryGetPropertySegment(property);
    }

    public static OpcUaNodeAttribute? TryGetOpcUaNodeAttribute(this RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is OpcUaNodeAttribute nodeAttribute)
            {
                return nodeAttribute;
            }
        }

        return null;
    }
}

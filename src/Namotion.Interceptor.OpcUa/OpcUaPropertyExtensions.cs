using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaPropertyExtensions
{
    public static string? ResolvePropertyName(this RegisteredSubjectProperty property, IOpcUaNodeMapper nodeMapper)
    {
        var nodeConfiguration = nodeMapper.TryGetNodeConfiguration(property);
        if (nodeConfiguration == null)
        {
            return null; // Property not mapped
        }

        return nodeConfiguration.BrowseName ?? property.BrowseName;
    }

    public static bool IsPropertyIncluded(this RegisteredSubjectProperty property, IOpcUaNodeMapper nodeMapper)
    {
        return nodeMapper.TryGetNodeConfiguration(property) != null;
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

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

    /// <summary>
    /// Finds the property marked with [OpcUaValue] (IsValue = true) in the given subject.
    /// Returns null if no value property is found.
    /// </summary>
    public static RegisteredSubjectProperty? TryGetValueProperty(this RegisteredSubject subject, IOpcUaNodeMapper nodeMapper)
    {
        foreach (var property in subject.Properties)
        {
            var config = nodeMapper.TryGetNodeConfiguration(property);
            if (config?.IsValue == true)
            {
                return property;
            }
        }

        return null;
    }
}

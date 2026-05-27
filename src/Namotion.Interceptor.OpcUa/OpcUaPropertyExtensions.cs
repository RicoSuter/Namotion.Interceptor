using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaPropertyExtensions
{
    public static string? ResolvePropertyName(this RegisteredSubjectProperty property, IPropertyMapper<OpcUaPropertyMapping> nodeMapper, IInterceptorSubject rootSubject)
    {
        if (!nodeMapper.TryGetMapping(property, rootSubject, out var mapping))
            return null;

        return mapping.BrowseName ?? property.BrowseName;
    }

    public static bool IsPropertyIncluded(this RegisteredSubjectProperty property, IPropertyMapper<OpcUaPropertyMapping> nodeMapper, IInterceptorSubject rootSubject)
    {
        return nodeMapper.TryGetMapping(property, rootSubject, out _);
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

    public static RegisteredSubjectProperty? TryGetValueProperty(this RegisteredSubject subject, IPropertyMapper<OpcUaPropertyMapping> nodeMapper, IInterceptorSubject rootSubject)
    {
        foreach (var property in subject.Properties)
        {
            if (nodeMapper.TryGetMapping(property, rootSubject, out var mapping) && mapping.IsValue == true)
            {
                return property;
            }
        }

        return null;
    }
}

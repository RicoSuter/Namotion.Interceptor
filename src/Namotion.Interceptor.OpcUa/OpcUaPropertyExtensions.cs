using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors.Paths;

namespace Namotion.Interceptor.OpcUa;

internal static class OpcUaPropertyExtensions
{
    public static string? ResolvePropertyName(this RegisteredSubjectProperty property, IConnectorPathProvider connectorPathProvider)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            var propertyName = connectorPathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;

            // TODO: Create property reference node instead of __?
            return ResolvePropertyName(attributedProperty, connectorPathProvider) + "__" + propertyName;
        }

        return connectorPathProvider.TryGetPropertySegment(property);
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

using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.OpcUa.Common;

internal static class OpcUaPropertyExtensions
{
    public static string? ResolvePropertyName(this RegisteredSubjectProperty property, ISourcePathProvider sourcePathProvider)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            var propertyName = sourcePathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;

            // TODO: Create property reference node instead of __?
            return ResolvePropertyName(attributedProperty, sourcePathProvider) + "__" + propertyName;
        }

        return sourcePathProvider.TryGetPropertySegment(property);
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

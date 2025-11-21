using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors.Paths.Attributes;

namespace Namotion.Interceptor.Connectors.Paths;

public class AttributeBasedConnectorPathProvider : ConnectorPathProviderBase
{
    private readonly string _sourceName;
    private readonly string? _pathPrefix;
    private readonly string _propertyPathDelimiter;
    private readonly string _attributePathDelimiter;

    public AttributeBasedConnectorPathProvider(string sourceName, string delimiter, string? pathPrefix = null)
    {
        _sourceName = sourceName;
        _propertyPathDelimiter = delimiter;
        _attributePathDelimiter = delimiter;
        _pathPrefix = pathPrefix ?? string.Empty;
    }

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        if (TryGetSourcePathAttribute(property) is null)
        {
            return false;
        }

        if (property.Parent.Parents.Length == 0)
        {
            return true;
        }

        foreach (var parent in property.Parent.Parents)
        {
            if (TryGetSourcePathAttribute(parent.Property) is not null)
            {
                return true;
            }
        }

        return false;
    }

    public override IEnumerable<(string segment, object? index)> ParsePathSegments(string path)
    {
        // remove prefix
        if (!string.IsNullOrEmpty(_pathPrefix))
        {
            if (!path.StartsWith(_pathPrefix))
            {
                // does not start with prefix, ignore this path
                return [];
            }

            path = path[_pathPrefix.Length..];
        }

        return path
            .Split(_propertyPathDelimiter)
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(s => s
                .Split(_attributePathDelimiter)
                .Select(ss =>
                {
                    var segmentParts = ss.Split('[', ']');
                    object? index = segmentParts.Length >= 2 ?
                        (int.TryParse(segmentParts[1], out var intIndex) ?
                            intIndex : segmentParts[1]) : null;

                    var currentPath = segmentParts[0];
                    return (currentPath, index);
                }));
    }

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        return TryGetSourcePathAttribute(property)?.Path;
    }

    public override string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> propertiesInPath)
    {
        var last = propertiesInPath.Last();
        return _pathPrefix + GetAttributeBasedSourcePropertyPath(last.property) + (last.index is not null ? $"[{last.index}]" : "");
    }

    private string GetAttributeBasedSourcePropertyPath(RegisteredSubjectProperty property)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            return GetAttributeBasedSourcePropertyPath(attributedProperty) + _attributePathDelimiter + TryGetPropertySegment(property);
        }

        var sourcePath = TryGetSourcePathAttribute(property)?.Path;
        var prefix = TryGetAttributeBasedSourcePathPrefix(property);
        return (prefix is not null ? prefix + _propertyPathDelimiter : "") + sourcePath;
    }

    private string? TryGetAttributeBasedSourcePathPrefix(RegisteredSubjectProperty property)
    {
        foreach (var parent in property.Parent.Parents)
        {
            var attribute = TryGetSourcePathAttribute(parent.Property);
            if (attribute is not null)
            {
                var prefix = TryGetAttributeBasedSourcePathPrefix(parent.Property);
                return
                    (prefix is not null ? prefix + _propertyPathDelimiter : "") +
                    attribute.Path +
                    (parent.Index is not null ? $"[{parent.Index}]" : "");
            }
        }

        return null;
    }

    private SourcePathAttribute? TryGetSourcePathAttribute(RegisteredSubjectProperty property)
    {
        var attributes = property.ReflectionAttributes;
        if (attributes is Attribute[] attrArray)
        {
            for (var i = 0; i < attrArray.Length; i++)
            {
                if (attrArray[i] is SourcePathAttribute spa && spa.SourceName == _sourceName)
                {
                    return spa;
                }
            }
            return null;
        }

        foreach (var attr in attributes)
        {
            if (attr is SourcePathAttribute spa && spa.SourceName == _sourceName)
            {
                return spa;
            }
        }

        return null;
    }
}

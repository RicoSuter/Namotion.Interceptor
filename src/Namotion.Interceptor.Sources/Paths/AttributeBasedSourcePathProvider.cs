using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.Sources.Paths;

public class AttributeBasedSourcePathProvider : SourcePathProviderBase
{
    private readonly string _sourceName;
    private readonly string? _pathPrefix;
    private readonly string _propertyPathDelimiter;
    private readonly string _attributePathDelimiter;

    public AttributeBasedSourcePathProvider(string sourceName, string delimiter, string? pathPrefix = null)
    {
        _sourceName = sourceName;
        _propertyPathDelimiter = delimiter;
        _attributePathDelimiter = delimiter;
        _pathPrefix = pathPrefix ?? string.Empty;
    }

    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return TryGetSourcePathAttribute(property) is not null && 
            (property.Parent.Parents.Count == 0 || 
             property.Parent.Parents.Any(p => TryGetSourcePathAttribute(p.Property) is not null));
    }
    
    public override IEnumerable<(string path, object? index)> ParsePathSegments(string path)
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
                .Select((ss, i) =>
                {
                    var segmentParts = ss.Split('[', ']');
                    object? index = segmentParts.Length >= 2 ? 
                        (int.TryParse(segmentParts[1], out var intIndex) ? 
                            intIndex : segmentParts[1]) : null;

                    var currentPath = segmentParts[0];
                    return (currentPath, index);
                }));
    }

    public override string? TryGetPropertyName(RegisteredSubjectProperty property)
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
        if (property is RegisteredSubjectAttribute attribute)
        {
            var attributedProperty = attribute.GetAttributedProperty();
            return GetAttributeBasedSourcePropertyPath(attributedProperty) + _attributePathDelimiter + TryGetPropertyName(property);
        }
        
        var sourcePath = TryGetSourcePathAttribute(property)?.Path;
        var prefix = TryGetAttributeBasedSourcePathPrefix(property);
        return (prefix is not null ? prefix + _propertyPathDelimiter : "") + sourcePath;
    }

    private string? TryGetAttributeBasedSourcePathPrefix(RegisteredSubjectProperty property)
    {
        var attribute = property.Parent
            .Parents
            .Select(p => new { property = p, attribute = TryGetSourcePathAttribute(p.Property) })
            .FirstOrDefault(p => p.attribute is not null);

        if (attribute is not null)
        {
            var prefix = TryGetAttributeBasedSourcePathPrefix(attribute.property.Property);
            return 
                (prefix is not null ? prefix + _propertyPathDelimiter : "") + 
                attribute.attribute!.Path + 
                (attribute.property.Index is not null ? $"[{attribute.property.Index}]" : "");
        }
        
        return null;
    }

    private SourcePathAttribute? TryGetSourcePathAttribute(RegisteredSubjectProperty property)
    {
        return property
            .ReflectionAttributes
            .OfType<SourcePathAttribute>()
            .FirstOrDefault(a => a.SourceName == _sourceName);
    }
}

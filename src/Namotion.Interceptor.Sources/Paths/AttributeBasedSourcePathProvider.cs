using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Paths.Attributes;
using Namotion.Interceptor.Tracking.Parent;

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
        return TryGetPropertyName(property) is not null;
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
        var nameAttribute = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == _sourceName);

        var pathAttribute = property
            .Attributes
            .OfType<SourcePathAttribute>()
            .FirstOrDefault(a => a.SourceName == _sourceName);

        return nameAttribute?.Path ?? pathAttribute?.Path;
    }
    
    public override string GetPropertyFullPath(string path, RegisteredSubjectProperty property, object? index)
    {
        return _pathPrefix + GetAttributeBasedSourcePropertyPath(property) + (index is not null ? $"[{index}]" : "");
    }

    private string GetAttributeBasedSourcePropertyPath(RegisteredSubjectProperty property)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            return GetAttributeBasedSourcePropertyPath(attributedProperty) + _attributePathDelimiter + TryGetPropertyName(property);
        }
        
        var sourcePath = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == _sourceName)?
            .Path;

        if (sourcePath is null)
        {
            sourcePath = property
                .Attributes
                .OfType<SourcePathAttribute>()
                .First(a => a.SourceName == _sourceName)?
                .Path;
        }

        var prefix = TryGetAttributeBasedSourcePathPrefix(property.Property);
        return (prefix is not null ? prefix + _propertyPathDelimiter : "") + sourcePath;
    }

    private string? TryGetAttributeBasedSourcePathPrefix(PropertyReference property)
    {
        var attribute = property.Subject
            .GetParents()
            .SelectMany(p => p.Property.Metadata
                .Attributes
                .OfType<SourcePathAttribute>()
                .Where(a => a.SourceName == _sourceName)
                .Select(a => new { p, a }) ?? [])
            .FirstOrDefault();

        if (attribute is not null)
        {
            var prefix = TryGetAttributeBasedSourcePathPrefix(attribute.p.Property);
            return 
                (prefix is not null ? prefix + _propertyPathDelimiter : "") + 
                attribute.a.Path + 
                (attribute.p.Index is not null ? $"[{attribute.p.Index}]" : "");
        }
        
        return null;
    }
}

using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Sources.Paths;

public class AttributeBasedSourcePathProvider : ISourcePathProvider
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
    
    public IEnumerable<(string path, object? index, bool isAttribute)> ParsePathSegments(string path)
    {
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
                    return (segmentParts[0], index, i > 0);
                }));
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return TryGetPropertySegmentName(property) is not null;
    }

    public string? TryGetPropertySegmentName(RegisteredSubjectProperty property)
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
    
    public string GetPropertyAttributePath(string path, RegisteredSubjectProperty attribute)
    {
        return path + _attributePathDelimiter + attribute.Attribute.AttributeName;
    }

    public string GetPropertyPath(string path, RegisteredSubjectProperty property)
    {
        var actualPath = GetAttributeBasedSourcePropertyPath(property, _sourceName);
        return _pathPrefix + actualPath;
    }

    private string GetAttributeBasedSourcePropertyPath(RegisteredSubjectProperty property, string sourceName)
    {
        var sourcePath = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == sourceName)?
            .Path;

        if (sourcePath is null)
        {
            sourcePath = property
                .Attributes
                .OfType<SourcePathAttribute>()
                .First(a => a.SourceName == sourceName)?
                .Path;
        }

        var prefix = TryGetAttributeBasedSourcePathPrefix(property.Property, sourceName);
        return (prefix is not null ? prefix + _propertyPathDelimiter : "") + sourcePath;
    }

    private string? TryGetAttributeBasedSourcePathPrefix(PropertyReference property, string sourceName)
    {
        var attribute = property.Subject
            .GetParents()
            .SelectMany(p => p.Property.Metadata
                .Attributes
                .OfType<SourcePathAttribute>()
                .Where(a => a.SourceName == sourceName)
                .Select(a => new { p, a }) ?? [])
            .FirstOrDefault();

        if (attribute is not null)
        {
            var prefix = TryGetAttributeBasedSourcePathPrefix(attribute.p.Property, sourceName);
            return 
                (prefix is not null ? prefix + _propertyPathDelimiter : "") + 
                attribute.a.Path + 
                (attribute.p.Index is not null ? $"[{attribute.p.Index}]" : "");
        }
        
        return null;
    }
}

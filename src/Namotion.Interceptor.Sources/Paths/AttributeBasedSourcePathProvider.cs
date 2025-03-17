using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Sources.Paths;

public class AttributeBasedSourcePathProvider : ISourcePathProvider
{
    private readonly string _sourceName;
    private readonly string _delimiter;
    private readonly string? _pathPrefix;

    public AttributeBasedSourcePathProvider(string sourceName, string delimiter, string? pathPrefix = null)
    {
        _sourceName = sourceName;
        _delimiter = delimiter;
        _pathPrefix = pathPrefix ?? string.Empty;
    }

    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        return property.IsAttribute || property.Attributes.Any(a => a is SourceNameAttribute || a is SourcePathAttribute);
    }

    public string? TryGetSourcePathSegmentName(RegisteredSubjectProperty property)
    {
        return TryGetAttributeBasedSourcePathSegmentName(property, _sourceName);
    }

    public string? TryGetSourcePropertyPath(string proposedPath, RegisteredSubjectProperty property)
    {
        if (property.IsAttribute)
            return proposedPath;
        
        var path = TryGetAttributeBasedSourcePropertyPath(property, _sourceName);
        return path is not null ? _pathPrefix + path : null;
    }

    private string? TryGetAttributeBasedSourcePathSegmentName(RegisteredSubjectProperty property, string sourceName)
    {
        var nameAttribute = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == sourceName);

        var pathAttribute = property
            .Attributes
            .OfType<SourcePathAttribute>()
            .FirstOrDefault(a => a.SourceName == sourceName);

        return nameAttribute?.Path ?? pathAttribute?.Path;
    }

    private string? TryGetAttributeBasedSourcePropertyPath(RegisteredSubjectProperty property, string sourceName)
    {
        var propertyName = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == sourceName)?
            .Path;

        if (propertyName is not null)
        {
            var prefix = TryGetAttributeBasedSourcePathPrefix(property.Property, sourceName);
            return (prefix is not null ? prefix + _delimiter : "") + propertyName;
        }
        
        return null;
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
                (prefix is not null ? prefix + _delimiter : "") + 
                attribute.a.Path + 
                (attribute.p.Index is not null ? $"[{attribute.p.Index}]" : "");
        }
        
        return null;
    }
}

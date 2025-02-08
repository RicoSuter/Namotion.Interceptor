using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Sources;

public class AttributeBasedSourcePathProvider : ISourcePathProvider
{
    private readonly string _sourceName;
    private readonly string? _pathPrefix;

    public AttributeBasedSourcePathProvider(string sourceName, string? pathPrefix = null)
    {
        _sourceName = sourceName;
        _pathPrefix = pathPrefix ?? string.Empty;
    }

    public string? TryGetSourcePathSegmentName(PropertyReference property)
    {
        var registry = property.Subject.Context.GetService<ISubjectRegistry>();
        var registeredProperty = registry.KnownSubjects[property.Subject].Properties[property.Name];
        return TryGetAttributeBasedSourcePathSegmentName(registeredProperty, _sourceName);
    }

    public string? TryGetSourcePropertyPath(PropertyReference property)
    {
        var registry = property.Subject.Context.GetService<ISubjectRegistry>();
        var registeredProperty = registry.KnownSubjects[property.Subject].Properties[property.Name];
        var path = TryGetAttributeBasedSourcePropertyPath(registeredProperty, _sourceName);
        return path is not null ? _pathPrefix + path : null;
    }

    private static string? TryGetAttributeBasedSourcePathSegmentName(RegisteredSubjectProperty property, string sourceName)
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

    private static string? TryGetAttributeBasedSourcePropertyPath(RegisteredSubjectProperty property, string sourceName)
    {
        var propertyName = property
            .Attributes
            .OfType<SourceNameAttribute>()
            .FirstOrDefault(a => a.SourceName == sourceName)?
            .Path;

        if (propertyName is not null)
        {
            var prefix = TryGetAttributeBasedSourcePathPrefix(property.Property, sourceName);
            return (prefix is not null ? prefix + "." : "") + propertyName;
        }
        
        return null;
    }

    private static string? TryGetAttributeBasedSourcePathPrefix(PropertyReference property, string sourceName)
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
                (prefix is not null ? prefix + "." : "") + 
                attribute.a.Path + 
                (attribute.p.Index is not null ? $"[{attribute.p.Index}]" : "");
        }
        
        return null;
    }
}

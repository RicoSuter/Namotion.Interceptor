using Namotion.Interceptor;
using Namotion.Proxy.Sources.Abstractions;

namespace Namotion.Proxy.Sources;

public class AttributeBasedSourcePathProvider : ISourcePathProvider
{
    private readonly string _sourceName;

    private readonly IProxyContext _context;
    private readonly string? _pathPrefix;

    public AttributeBasedSourcePathProvider(string sourceName, IProxyContext context, string? pathPrefix = null)
    {
        _sourceName = sourceName;
        _context = context;
        _pathPrefix = pathPrefix ?? string.Empty;
    }

    public string? TryGetSourcePropertyName(PropertyReference property)
    {
        return property.TryGetAttributeBasedSourcePropertyName(_sourceName);
    }

    public string? TryGetSourcePath(PropertyReference property)
    {
        var path = property.TryGetAttributeBasedSourcePath(_sourceName, _context);
        return path is not null ? _pathPrefix + path : null;
    }
}

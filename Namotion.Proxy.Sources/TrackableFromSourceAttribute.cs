using Namotion.Proxy;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Sources;

namespace Namotion.Proxy.Sources;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class TrackableSourceAttribute : Attribute
{
    public string SourceName { get; }

    public string? Path { get; }

    public string? AbsolutePath { get; set; }

    public TrackableSourceAttribute(string sourceName, string? path = null)
    {
        SourceName = sourceName;
        Path = path;
    }

    //public void InitializeProperty(ProxyProperty property, object? parentCollectionKey, IProxyContext context)
    //{
    //    var parentPath = property.Parent.Parents.FirstOrDefault().TryGetAttributeBasedSourcePathPrefix(SourceName) +
    //        (parentCollectionKey != null ? $"[{parentCollectionKey}]" : string.Empty);

    //    var sourcePath = GetSourcePath(parentPath, property);
    //    property.SetAttributeBasedSourcePath(SourceName, sourcePath);
    //    property.SetAttributeBasedSourceProperty(SourceName, Path ?? property.Name);
    //}

    private string GetSourcePath(string? basePath, ProxyPropertyReference property)
    {
        if (AbsolutePath != null)
        {
            return AbsolutePath!;
        }
        else if (Path != null)
        {
            return (!string.IsNullOrEmpty(basePath) ? basePath + "." : "") + Path;
        }

        return (!string.IsNullOrEmpty(basePath) ? basePath + "." : "") + property.PropertyName;
    }
}

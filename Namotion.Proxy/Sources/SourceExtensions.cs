using Namotion.Proxy.Abstractions;
using System.Collections.Immutable;

namespace Namotion.Proxy.Sources;

public static class SourceExtensions
{
    private const string SourcePropertyNameKey = "SourcePropertyName:";
    private const string SourcePathKey = "SourcePath:";
    private const string SourcePathPrefixKey = "SourcePathPrefix:";

    private const string IsChangingFromSourceKey = "IsChangingFromSource";

    public static string? TryGetAttributeBasedSourceProperty(this ProxyPropertyReference property, string sourceName)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePropertyNameKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePath(this ProxyPropertyReference property, string sourceName, IProxyContext context)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePathKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePathPrefix(this ProxyPropertyReference property, string sourceName)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePathPrefixKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static void SetAttributeBasedSourceProperty(this ProxyPropertyReference property, string sourceName, string sourceProperty)
    {
        property.Proxy.Data[$"{SourcePropertyNameKey}{property.PropertyName}.{sourceName}"] = sourceProperty;
    }

    public static void SetAttributeBasedSourcePathPrefix(this ProxyPropertyReference property, string sourceName, string sourcePath)
    {
        property.Proxy.Data[$"{SourcePathPrefixKey}{property.PropertyName}.{sourceName}"] = sourcePath;
    }

    public static void SetAttributeBasedSourcePath(this ProxyPropertyReference property, string sourceName, string sourcePath)
    {
        property.Proxy.Data[$"{SourcePathKey}{property.PropertyName}.{sourceName}"] = sourcePath;
    }

    public static void SetValueFromSource(this ProxyPropertyReference property, ITrackableSource source, object? valueFromSource)
    {
        //lock (property.Data)
        //{
        //    property.Data = property.Data.SetItem(IsChangingFromSourceKey,
        //        property.Data.TryGetValue(IsChangingFromSourceKey, out var sources)
        //        ? ((ITrackableSource[])sources!).Append(source).ToArray()
        //        : (object)(new[] { source }));
        //}

        //try
        //{
        //    var newValue = valueFromSource;

        //    var currentValue = property.Proxy.Properties[property.PropertyName].GetValue(property.Proxy);
        //    if (!Equals(currentValue, newValue))
        //    {
        //        property.SetValue(newValue);
        //    }
        //}
        //finally
        //{
        //    lock (property.Data)
        //    {
        //        property.Data = property.Data.SetItem(IsChangingFromSourceKey,
        //            ((ITrackableSource[])property.Data[IsChangingFromSourceKey]!)
        //            .Except(new[] { source })
        //            .ToArray());
        //    }
        //}
    }

    public static bool IsChangingFromSource(this ProxyPropertyChanged change, ITrackableSource source)
    {
        //return change.PropertyDataSnapshot.TryGetValue(IsChangingFromSourceKey, out var isChangingFromSource) &&
        //    isChangingFromSource is ITrackableSource[] sources &&
        //    sources.Contains(source);
        return false;
    }
}

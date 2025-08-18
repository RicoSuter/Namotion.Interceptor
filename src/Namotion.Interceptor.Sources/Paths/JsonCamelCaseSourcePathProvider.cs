using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public class JsonCamelCaseSourcePathProvider : SourcePathProviderBase
{
    public static JsonCamelCaseSourcePathProvider Instance { get; } = new();

    public override string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> propertiesInPath)
    {
        return propertiesInPath.Aggregate("", 
            (path, tuple) => (string.IsNullOrEmpty(path) ? "" : path + ".") + ConvertToSourcePath(tuple.property.BrowseName) + (tuple.index is not null ? $"[{tuple.index}]" : ""));
    }

    /// <inheritdoc />
    public override IEnumerable<(string segment, object? index)> ParsePathSegments(string path)
    {
        return base.ParsePathSegments(path)
            .Select(t => (ConvertFromSourcePath(t.segment), t.index));
    }

    public static string ConvertToSourcePath(string path)
    {
        return path.Length > 1 ? char.ToLowerInvariant(path[0]) + path[1..] : path.ToLowerInvariant();
    }

    public static string ConvertFromSourcePath(string path)
    {
        return path.Length > 1 ? char.ToUpperInvariant(path[0]) + path[1..] : path.ToUpperInvariant();
    }
}
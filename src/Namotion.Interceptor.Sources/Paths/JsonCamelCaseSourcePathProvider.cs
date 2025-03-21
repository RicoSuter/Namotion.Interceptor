using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public class JsonCamelCaseSourcePathProvider : SourcePathProviderBase
{
    public static JsonCamelCaseSourcePathProvider Instance { get; } = new();

    public override string GetPropertyFullPath(string path, RegisteredSubjectProperty property)
    {
        return path + ConvertToSourcePath(property.BrowseName);
    }

    /// <inheritdoc />
    public override IEnumerable<(string path, object? index)> ParsePathSegments(string path)
    {
        return base.ParsePathSegments(path)
            .Select(t => (ConvertFromSourcePath(t.path), t.index));
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
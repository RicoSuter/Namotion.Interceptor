using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

public class JsonCamelCaseSourcePathProvider : ISourcePathProvider
{
    public static JsonCamelCaseSourcePathProvider Instance { get; } = new();

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return true;
    }

    public string? TryGetPropertyName(RegisteredSubjectProperty property)
    {
        return property.BrowseName;
    }
    
    public string GetPropertyFullPath(string path, RegisteredSubjectProperty property)
    {
        return path + ConvertToSourcePath(property.BrowseName);
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
namespace Namotion.Interceptor.Sources.Paths.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class SourcePathAttribute : Attribute
{
    public string SourceName { get; }

    public string? Path { get; }

    public SourcePathAttribute(string sourceName, string? path = null)
    {
        SourceName = sourceName;
        Path = path;
    }
}

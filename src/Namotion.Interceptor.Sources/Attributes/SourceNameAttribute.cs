namespace Namotion.Interceptor.Sources.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class SourceNameAttribute : Attribute
{
    public string SourceName { get; }

    public string Path { get; }
    
    public SourceNameAttribute(string sourceName, string path)
    {
        SourceName = sourceName;
        Path = path;
    }
}

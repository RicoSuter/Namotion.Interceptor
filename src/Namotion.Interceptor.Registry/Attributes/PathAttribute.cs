namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Specifies a custom path segment for a property for a specific named context.
/// Multiple attributes can be applied with different names.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public class PathAttribute : Attribute
{
    /// <summary>
    /// Creates a path mapping for a specific named context.
    /// </summary>
    /// <param name="name">The context name (e.g., "mqtt", "opcua", "json").</param>
    /// <param name="path">The path segment for this property.</param>
    public PathAttribute(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>
    /// Gets the context name this path mapping applies to.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the path segment for this property.
    /// </summary>
    public string Path { get; }
}

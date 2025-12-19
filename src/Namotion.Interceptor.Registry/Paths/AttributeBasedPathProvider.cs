using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Path provider that uses [Path] attributes for custom segment mapping.
/// Requires a name to filter attributes. Returns null for properties without matching [Path] attribute.
/// </summary>
public class AttributeBasedPathProvider : PathProviderBase
{
    private readonly string _name;
    private readonly char _pathSeparator;

    /// <summary>
    /// Creates a provider that filters [Path] attributes by name.
    /// </summary>
    /// <param name="name">The context name to filter by (e.g., "mqtt", "opcua").</param>
    /// <param name="pathSeparator">The path separator character. Default is '.'.</param>
    public AttributeBasedPathProvider(string name, char pathSeparator = '.')
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _pathSeparator = pathSeparator;
    }

    /// <summary>
    /// Gets the context name this provider filters by.
    /// </summary>
    public string Name => _name;

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    /// <remarks>
    /// Includes properties that have a [Path] attribute with matching name,
    /// or properties marked with [Children] for path resolution.
    /// </remarks>
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        // Include if has matching [Path] attribute
        var hasPathAttribute = property.ReflectionAttributes
            .OfType<PathAttribute>()
            .Any(a => a.Name == _name);

        if (hasPathAttribute)
            return true;

        // Also include [Children] properties for path resolution (transparent containers)
        return property.ReflectionAttributes
            .OfType<ChildrenAttribute>()
            .Any();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the [Path] attribute value matching the name, or null if no match.
    /// Use <see cref="IsPropertyIncluded"/> to check if a property should be monitored/exposed.
    /// </remarks>
    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var pathAttribute = property.ReflectionAttributes
            .OfType<PathAttribute>()
            .FirstOrDefault(a => a.Name == _name);

        return pathAttribute?.Path;
    }
}

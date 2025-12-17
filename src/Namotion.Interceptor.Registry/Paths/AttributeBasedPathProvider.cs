using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Path provider that uses [Path] attributes for custom segment mapping.
/// Requires a name to filter attributes. Falls back to BrowseName when no matching attribute found.
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
    /// Only includes properties that have a [Path] attribute with matching name.
    /// </remarks>
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return property.ReflectionAttributes
            .OfType<PathAttribute>()
            .Any(a => a.Name == _name);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the [Path] attribute value matching the name, or BrowseName if no match.
    /// The fallback to BrowseName enables path building through parent properties without attributes.
    /// Use <see cref="IsPropertyIncluded"/> to check if a property should be monitored/exposed.
    /// </remarks>
    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var pathAttribute = property.ReflectionAttributes
            .OfType<PathAttribute>()
            .FirstOrDefault(a => a.Name == _name);

        return pathAttribute?.Path ?? property.BrowseName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// First looks for a property with a matching [Path] attribute,
    /// then falls back to matching by BrowseName,
    /// then falls back to [Children] dictionary lookup.
    /// </remarks>
    public override RegisteredSubjectProperty? TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment)
    {
        // Look for property with matching [Path] attribute for our name
        foreach (var property in subject.Properties)
        {
            var pathAttribute = property.ReflectionAttributes
                .OfType<PathAttribute>()
                .FirstOrDefault(a => a.Name == _name);

            if (pathAttribute?.Path == segment)
            {
                return property;
            }
        }

        // [Children] fallback: Segment is a dictionary key (children property needs [Path] attribute on it)
        var childrenPropertyName = ChildrenAttribute.GetChildrenPropertyName(subject.Subject.GetType());
        if (childrenPropertyName is not null)
        {
            return subject.TryGetProperty(childrenPropertyName);
        }

        return null;
    }
}

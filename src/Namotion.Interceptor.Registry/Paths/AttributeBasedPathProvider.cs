using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Path provider that uses [Path] attributes for custom segment mapping.
/// Requires a name to filter attributes. Excludes properties without a matching [Path] attribute, except an [InlinePaths] property.
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
    /// Includes a property that has a [Path] attribute with a matching name, with the attribute's path as segment, and
    /// an [InlinePaths] property without one, with its BrowseName as segment.
    /// </remarks>
    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        var attribute = FindPathAttribute(property.ReflectionAttributes, out var hasInlinePaths);
        segment = attribute is not null ? attribute.Path ?? property.BrowseName : hasInlinePaths ? property.BrowseName : null;
        return segment is not null;
    }

    // Indexed rather than enumerated: this runs for every property of a subject on each segment lookup, and an
    // interface enumerator would allocate per call.
    private PathAttribute? FindPathAttribute(IReadOnlyCollection<Attribute> attributes, out bool hasInlinePaths)
    {
        hasInlinePaths = false;
        if (attributes is IList<Attribute> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (Match(list[i], ref hasInlinePaths) is { } match)
                {
                    return match;
                }
            }

            return null;
        }

        foreach (var attribute in attributes)
        {
            if (Match(attribute, ref hasInlinePaths) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private PathAttribute? Match(Attribute attribute, ref bool hasInlinePaths)
    {
        if (attribute is PathAttribute pathAttribute && pathAttribute.Name == _name)
        {
            return pathAttribute;
        }

        hasInlinePaths |= attribute is InlinePathsAttribute;
        return null;
    }
}

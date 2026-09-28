using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Path provider that sources segments from a code-based <see cref="FluentMappingRegistry{TMetadata}"/> instead
/// of attributes. A drop-in replacement for <see cref="AttributeBasedPathProvider"/>: it inherits forward
/// composition, index handling, [InlinePaths] support, and segment-guided reverse lookup from
/// <see cref="PathProviderBase"/>. The type parameter only types the registry reference; the provider itself
/// is metadata-agnostic (it reads string segments).
/// </summary>
public sealed class FluentPathProvider<TMetadata> : PathProviderBase
    where TMetadata : class
{
    private readonly FluentMappingRegistry<TMetadata> _registry;
    private readonly char _pathSeparator;

    /// <summary>Creates a provider over the given fluent registry.</summary>
    /// <param name="registry">The fluent registry to read segments from.</param>
    /// <param name="pathSeparator">The path separator character. Default is '.'.</param>
    public FluentPathProvider(FluentMappingRegistry<TMetadata> registry, char pathSeparator = '.')
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        if (_registry.TryGetSegment(property.Subject.GetType(), property.Name, out _))
            return true;

        // Mirror AttributeBasedPathProvider: [InlinePaths] containers participate in path resolution.
        return property.ReflectionAttributes.OfType<InlinePathsAttribute>().Any();
    }

    /// <inheritdoc />
    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (_registry.TryGetSegment(property.Subject.GetType(), property.Name, out var segment))
            return segment ?? property.BrowseName;

        return null;
    }
}

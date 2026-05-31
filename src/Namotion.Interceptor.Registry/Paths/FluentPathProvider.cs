using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Path provider that sources segments from a code-based <see cref="IFluentSegmentSource"/> instead of
/// attributes. A drop-in replacement for <see cref="AttributeBasedPathProvider"/>: it inherits forward
/// composition, index handling, [InlinePaths] support, and segment-guided reverse lookup from
/// <see cref="PathProviderBase"/>.
/// </summary>
public sealed class FluentPathProvider : PathProviderBase
{
    private readonly IFluentSegmentSource _source;
    private readonly char _pathSeparator;

    /// <summary>Creates a provider over the given fluent segment source.</summary>
    /// <param name="source">The fluent registry to read segments from.</param>
    /// <param name="pathSeparator">The path separator character. Default is '.'.</param>
    public FluentPathProvider(IFluentSegmentSource source, char pathSeparator = '.')
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        if (_source.TryGetSegment(property.Subject.GetType(), property.Name, out _))
            return true;

        // Mirror AttributeBasedPathProvider: [InlinePaths] containers participate in path resolution.
        return property.ReflectionAttributes.OfType<InlinePathsAttribute>().Any();
    }

    /// <inheritdoc />
    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        if (_source.TryGetSegment(property.Subject.GetType(), property.Name, out var segment))
            return segment ?? property.BrowseName;

        return null;
    }
}

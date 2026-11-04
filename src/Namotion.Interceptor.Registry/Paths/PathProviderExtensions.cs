using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Inclusion checks of an <see cref="IPathProvider"/>.
/// </summary>
public static class PathProviderExtensions
{
    /// <summary>
    /// Gets whether the provider includes the property, see <see cref="IPathProvider.TryGetPropertySegment"/>.
    /// </summary>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="property">The property to check.</param>
    /// <returns>True when the property is included; otherwise, false.</returns>
    public static bool IsPropertyIncluded(this IPathProvider pathProvider, RegisteredSubjectProperty property)
        => pathProvider.TryGetPropertySegment(property, out _);

    /// <summary>
    /// Gets whether the property is effectively included: the provider includes it and, for an attribute, the property
    /// it belongs to, recursively up to a property that is no attribute. Attributes that belong to each other in a cycle
    /// are not included. The properties holding its subject are not checked. Paths pass only effectively included
    /// properties.
    /// </summary>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="property">The property to check.</param>
    /// <returns>True when the property is effectively included; otherwise, false.</returns>
    public static bool IsPropertyEffectivelyIncluded(this IPathProvider pathProvider, RegisteredSubjectProperty property)
        => pathProvider.TryGetEffectivelyIncludedSegment(property, out _);

    /// <summary>
    /// Gets whether the property is effectively included (see <see cref="IsPropertyEffectivelyIncluded"/>) and, when it
    /// is, its segment.
    /// </summary>
    internal static bool TryGetEffectivelyIncludedSegment(
        this IPathProvider pathProvider, RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        => TryGetEffectivelyIncludedSegment(new InterfaceSegments(pathProvider), property, out segment);

    /// <inheritdoc cref="TryGetEffectivelyIncludedSegment(IPathProvider, RegisteredSubjectProperty, out string?)"/>
    internal static bool TryGetEffectivelyIncludedSegment(
        this PathProviderBase pathProvider, RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        => TryGetEffectivelyIncludedSegment(new BaseSegments(pathProvider), property, out segment);

    // Generic over a struct so the path walk, which holds a PathProviderBase, calls the segment method virtually
    // rather than through the interface.
    private static bool TryGetEffectivelyIncludedSegment<TSegments>(
        TSegments segments, RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        where TSegments : struct, ISegments
    {
        if (segments.TryGetPropertySegment(property, out segment) &&
            (!property.IsAttribute || IsPropertyOfAttributeIncluded(segments, property)))
        {
            return true;
        }

        segment = null;
        return false;
    }

    private static bool IsPropertyOfAttributeIncluded<TSegments>(TSegments segments, RegisteredSubjectProperty attribute)
        where TSegments : struct, ISegments
    {
        // Attributes can name each other in a cycle; a chain longer than the subject's properties is one, and it
        // belongs to no included property.
        var remainingSteps = attribute.Parent.Properties.Length;
        var property = attribute;
        do
        {
            // An attribute whose property is missing belongs to no included property.
            property = property.Parent.TryGetProperty(property.AttributeMetadata.PropertyName);
            if (property is null || --remainingSteps < 0 || !segments.TryGetPropertySegment(property, out _))
            {
                return false;
            }
        }
        while (property.IsAttribute);

        return true;
    }

    private interface ISegments
    {
        bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment);
    }

    private readonly struct InterfaceSegments(IPathProvider pathProvider) : ISegments
    {
        public bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
            => pathProvider.TryGetPropertySegment(property, out segment);
    }

    private readonly struct BaseSegments(PathProviderBase pathProvider) : ISegments
    {
        public bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
            => pathProvider.TryGetPropertySegment(property, out segment);
    }
}

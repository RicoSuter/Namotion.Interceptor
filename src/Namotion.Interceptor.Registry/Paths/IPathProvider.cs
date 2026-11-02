using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Maps between subject properties and external path segments, and decides which properties paths include.
/// Used by both sources (inbound sync) and servers (outbound exposure). Both methods may be called concurrently.
/// </summary>
/// <remarks>
/// The provider decides about each property on its own. Paths also exclude an attribute whose property is excluded,
/// which the registry applies, see <see cref="PathProviderExtensions.IsPropertyEffectivelyIncluded"/>.
/// </remarks>
public interface IPathProvider
{
    /// <summary>
    /// Gets whether the property is included and, when it is, its path segment.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="segment">The segment of the included property, never null when the method returns true; otherwise, null.</param>
    /// <returns>True when the property is included; false when it is excluded.</returns>
    /// <remarks>
    /// A segment must not contain the path separator to resolve back through <see cref="TryGetPropertyFromSegment"/>.
    /// </remarks>
    bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment);

    /// <summary>
    /// Finds a property by its path segment, the inverse of <see cref="TryGetPropertySegment"/>: the first included
    /// property, in the subject's property order, other than the [InlinePaths] property whose segment equals
    /// <paramref name="segment"/>, otherwise the included [InlinePaths] property, whose own segment or entry key the
    /// segment is. Never returns an excluded property.
    /// </summary>
    /// <param name="subject">The subject to search in.</param>
    /// <param name="segment">The path segment to look up.</param>
    /// <param name="property">The matching property, or null when none matches.</param>
    /// <returns>True when a property matches; otherwise, false.</returns>
    bool TryGetPropertyFromSegment(RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property);
}

using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Maps between subject properties and external path segments.
/// Used by both sources (inbound sync) and servers (outbound exposure).
/// </summary>
public interface IPathProvider
{
    /// <summary>
    /// Determines whether the specified property should be included in paths.
    /// </summary>
    /// <param name="property">The property to check.</param>
    /// <returns>True if the property should be included; otherwise, false.</returns>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Gets the path segment for a property.
    /// Returns null if no explicit mapping exists (e.g., no [Path] attribute).
    /// Override for camelCase, custom naming, [Path] attribute, etc.
    /// </summary>
    /// <param name="property">The property to get the segment for.</param>
    /// <returns>The path segment, or null if no explicit mapping exists.</returns>
    string? TryGetPropertySegment(RegisteredSubjectProperty property);

    /// <summary>
    /// Finds a property by its path segment.
    /// Override for camelCase conversion, [InlinePaths] fallback, etc.
    /// </summary>
    /// <param name="subject">The subject to search in.</param>
    /// <param name="segment">The path segment to look up.</param>
    /// <returns>The matching property, or null if not found.</returns>
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
}

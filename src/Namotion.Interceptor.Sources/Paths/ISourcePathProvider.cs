﻿using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Paths;

/// <summary>
/// Interface to map between source and subject paths.
/// </summary>
public interface ISourcePathProvider
{
    /// <summary>
    /// Checks whether the property handled by the source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The result.</returns>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    
    /// <summary>
    /// Gets the name of the property in the source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The property name.</returns>
    string? TryGetPropertyName(RegisteredSubjectProperty property);
    
    /// <summary>
    /// Gets the full path of the property in the source.
    /// </summary>
    /// <param name="propertiesInPath">The properties in the path.</param>
    /// <returns>The full path.</returns>
    string GetPropertyFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> propertiesInPath);

    /// <summary>
    /// Parses the full path into property segments.
    /// </summary>
    /// <param name="path">The path to parse.</param>
    /// <returns>The segments.</returns>
    IEnumerable<(string path, object? index)> ParsePathSegments(string path);

    /// <summary>
    /// Gets the attribute using the path segment name in the source.
    /// </summary>
    /// <param name="property">The property with the attribute.</param>
    /// <param name="segment">The path segment name.</param>
    /// <returns>The attribute property.</returns>
    RegisteredSubjectProperty? TryGetAttributeFromSegment(RegisteredSubjectProperty property, string segment);
    
    /// <summary>
    /// Gets the property using the path segment name in the source.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="segment">The path segment name.</param>
    /// <returns>The property.</returns>
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
}

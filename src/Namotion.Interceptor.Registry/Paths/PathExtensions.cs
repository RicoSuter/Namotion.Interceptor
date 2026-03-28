using System.Buffers;
using System.Collections;
using System.Text;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Extension methods for path operations on subjects and properties.
/// </summary>
public static class PathExtensions
{
    /// <summary>
    /// Parses a path string into segments with their indices.
    /// </summary>
    public static List<(string segment, object? index)> ParsePath(this PathProviderBase pathProvider, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var results = new List<(string segment, object? index)>();
        var separator = pathProvider.PathSeparator;
        var indexOpen = pathProvider.IndexOpen;
        var indexClose = pathProvider.IndexClose;
        var start = 0;

        for (var i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == separator)
            {
                if (i > start)
                {
                    results.Add(ParseSegment(path.AsSpan(start, i - start), indexOpen, indexClose));
                }
                start = i + 1;
            }
        }

        return results;
    }

    private static (string segment, object? index) ParseSegment(ReadOnlySpan<char> span, char indexOpen, char indexClose)
    {
        var bracketIndex = span.IndexOf(indexOpen);
        if (bracketIndex < 0)
        {
            return (span.ToString(), null);
        }

        var name = span[..bracketIndex].ToString();

        // Search for closing bracket after the opening bracket
        var afterOpen = span[(bracketIndex + 1)..];
        var closeBracket = afterOpen.IndexOf(indexClose);
        if (closeBracket <= 0)
        {
            return (name, null);
        }

        var indexSpan = afterOpen[..closeBracket];
        object? index = int.TryParse(indexSpan, out var intIndex) ? intIndex : indexSpan.ToString();
        return (name, index);
    }

    /// <summary>
    /// Tries to get a property from a path starting at the given subject.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The property and its last-segment index at the path, or null if not found.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetPropertyFromPath(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        string path)
    {
        var segments = pathProvider.ParsePath(path);
        if (segments.Count == 0)
        {
            return null;
        }

        var currentSubject = rootSubject;
        RegisteredSubjectProperty? currentProperty = null;
        object? lastIndex = null;

        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, index) = segments[i];
            currentProperty = pathProvider.TryGetPropertyFromSegment(currentSubject, segment);

            if (currentProperty is null)
            {
                return null;
            }

            // When the property is an [InlinePaths] dictionary and no bracket index
            // was provided, the segment name itself is the dictionary key.
            var effectiveIndex = index;
            if (effectiveIndex is null &&
                InlinePathsAttribute.IsInlinePathsProperty(
                    currentSubject.Subject.GetType(), currentProperty.Name))
            {
                effectiveIndex = segment;
            }

            lastIndex = effectiveIndex;

            // If not the last segment, navigate to the child subject
            if (i < segments.Count - 1)
            {
                var childSubject = GetChildSubject(currentProperty, effectiveIndex);
                var registeredChild = childSubject?.TryGetRegisteredSubject();
                if (registeredChild is null)
                {
                    return null;
                }

                currentSubject = registeredChild;
            }
        }

        return currentProperty is not null ? (currentProperty, lastIndex) : null;
    }

    /// <summary>
    /// Tries to get a subject from a path starting at the given subject.
    /// Handles indices on the last segment (e.g., "Children[key]" resolves to the child subject).
    /// For paths ending in a subject reference property without an index (e.g., "Device"),
    /// the referenced subject is returned.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The subject at the path, or null if not found.</returns>
    public static RegisteredSubject? TryGetSubjectFromPath(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        string path)
    {
        var result = pathProvider.TryGetPropertyFromPath(rootSubject, path);
        if (result is null)
        {
            return null;
        }

        var (property, index) = result.Value;
        var childSubject = GetChildSubject(property, index);
        return childSubject?.TryGetRegisteredSubject();
    }

    /// <summary>
    /// Gets all properties from a collection of paths.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="paths">The paths to resolve.</param>
    /// <returns>An enumerable of property and index tuples that were found.</returns>
    public static IEnumerable<(RegisteredSubjectProperty Property, object? Index)> GetPropertiesFromPaths(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var result = pathProvider.TryGetPropertyFromPath(rootSubject, path);
            if (result is not null)
            {
                yield return result.Value;
            }
        }
    }

    private static IInterceptorSubject? GetChildSubject(RegisteredSubjectProperty property, object? index)
    {
        var value = property.GetValue();
        if (value is null)
        {
            return null;
        }

        if (index is null)
        {
            return value as IInterceptorSubject;
        }

        if (property.IsSubjectDictionary && value is IDictionary dictionary)
        {
            return dictionary[index] as IInterceptorSubject;
        }

        if (property.IsSubjectCollection && value is IList list && index is int intIndex)
        {
            return list[intIndex] as IInterceptorSubject;
        }

        return null;
    }

    /// <summary>
    /// Gets the complete path of the given property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <param name="propertyIndex">Optional index for the property (e.g., dictionary key or collection index).
    /// When provided, the property path includes this index, which is useful for computing
    /// the path to a child subject held at a specific index within this property.</param>
    /// <returns>The path.</returns>
    public static string? TryGetPath(this RegisteredSubjectProperty property, PathProviderBase pathProvider, IInterceptorSubject? rootSubject, object? propertyIndex = null)
    {
        if (!pathProvider.IsPropertyIncluded(property))
        {
            return null;
        }

        var buffer = ArrayPool<(RegisteredSubjectProperty Property, object? Index)>.Shared.Rent(16);
        try
        {
            var count = 0;
            var current = property;
            object? pendingIndex = propertyIndex;

            while (current is not null)
            {
                if (count == buffer.Length)
                {
                    var newBuffer = ArrayPool<(RegisteredSubjectProperty, object?)>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, count).CopyTo(newBuffer);
                    ArrayPool<(RegisteredSubjectProperty, object?)>.Shared.Return(buffer);
                    buffer = newBuffer;
                }

                buffer[count++] = (current, pendingIndex);

                if (rootSubject is not null && current.Subject == rootSubject)
                {
                    break;
                }

                if (current.Parent.Parents.Length == 0)
                {
                    break;
                }

                var parent = current.Parent.Parents[0];
                pendingIndex = parent.Index;
                current = parent.Property;
            }

            var sb = new StringBuilder();
            for (var i = count - 1; i >= 0; i--)
            {
                var (prop, index) = buffer[i];

                // [InlinePaths] properties: emit just the index as a plain segment.
                // IsPropertyIncluded is not checked here because intermediate properties
                // are traversed for navigation, and PathProviderBase implementations
                // (e.g., AttributeBasedPathProvider) already include [InlinePaths] properties.
                if (index is not null &&
                    InlinePathsAttribute.IsInlinePathsProperty(
                        prop.Subject.GetType(), prop.Name))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(pathProvider.PathSeparator);
                    }

                    sb.Append(index);
                    continue;
                }

                var segment = pathProvider.TryGetPropertySegment(prop) ?? prop.BrowseName;
                if (sb.Length > 0)
                {
                    sb.Append(pathProvider.PathSeparator);
                }

                sb.Append(segment);
                if (index is not null)
                {
                    sb.Append(pathProvider.IndexOpen).Append(index).Append(pathProvider.IndexClose);
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        finally
        {
            ArrayPool<(RegisteredSubjectProperty, object?)>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Gets all complete paths of the given properties.
    /// </summary>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetPaths(
        this IEnumerable<RegisteredSubjectProperty> properties, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var property in properties)
        {
            var path = property.TryGetPath(pathProvider, rootSubject);
            if (path is not null)
            {
                yield return (path, property);
            }
        }
    }

    /// <summary>
    /// Gets all complete paths of the given property changes.
    /// </summary>
    public static IEnumerable<(string path, SubjectPropertyChange change)> GetPaths(
        this IEnumerable<SubjectPropertyChange> changes, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var change in changes)
        {
            var registeredProperty = change.Property.TryGetRegisteredProperty();
            if (registeredProperty is not null)
            {
                var path = registeredProperty.TryGetPath(pathProvider, rootSubject);
                if (path is not null)
                {
                    yield return (path, change);
                }
            }
        }
    }
}

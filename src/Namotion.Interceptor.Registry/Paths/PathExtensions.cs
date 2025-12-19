using System.Collections;
using System.Text;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Extension methods for path operations on subjects and properties.
/// </summary>
public static class PathExtensions
{
    /// <summary>
    /// Gets a full path string for a sequence of properties with their indices.
    /// </summary>
    public static string GetPath(
        this PathProviderBase pathProvider,
        IEnumerable<(RegisteredSubjectProperty property, object? index)> properties)
    {
        StringBuilder? sb = null;
        foreach (var (property, index) in properties)
        {
            // Use explicit segment or fall back to BrowseName for path building
            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

            sb ??= new StringBuilder();
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
        return sb?.ToString() ?? string.Empty;
    }

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
    /// <returns>The property at the path, or null if not found.</returns>
    public static RegisteredSubjectProperty? TryGetPropertyFromPath(
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

        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, index) = segments[i];
            currentProperty = pathProvider.TryGetPropertyFromSegment(currentSubject, segment);

            if (currentProperty is null)
            {
                return null;
            }

            // If not the last segment, navigate to the child subject
            if (i < segments.Count - 1)
            {
                var childSubject = GetChildSubject(currentProperty, index);
                var registeredChild = childSubject?.TryGetRegisteredSubject();
                if (registeredChild is null)
                {
                    return null;
                }

                currentSubject = registeredChild;
            }
        }

        return currentProperty;
    }

    /// <summary>
    /// Gets all properties from a collection of paths.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="paths">The paths to resolve.</param>
    /// <returns>An enumerable of properties that were found.</returns>
    public static IEnumerable<RegisteredSubjectProperty> GetPropertiesFromPaths(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var property = pathProvider.TryGetPropertyFromPath(rootSubject, path);
            if (property is not null)
            {
                yield return property;
            }
        }
    }

    /// <summary>
    /// Tries to build a path from the root to the given property.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="property">The property to build a path to.</param>
    /// <param name="rootSubject">The root subject (optional, defaults to traversing to root).</param>
    /// <returns>The path string, or null if unable to build path.</returns>
    public static string? TryGetPath(
        this PathProviderBase pathProvider,
        RegisteredSubjectProperty property,
        IInterceptorSubject? rootSubject = null)
    {
        var pathParts = new List<(RegisteredSubjectProperty property, object? index)>();
        var current = property;

        while (current is not null)
        {
            pathParts.Add((current, null));

            if (rootSubject is not null && current.Subject == rootSubject)
            {
                break;
            }

            // Find parent property that references this subject
            var parentInfo = FindParentProperty(current.Parent);
            if (parentInfo is null)
            {
                break;
            }

            var (parentProperty, index) = parentInfo.Value;
            if (pathParts.Count > 0)
            {
                // Update the last item with the index from parent
                pathParts[^1] = (pathParts[^1].property, index);
            }

            current = parentProperty;
        }

        pathParts.Reverse();
        return pathProvider.GetPath(pathParts);
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

    private static (RegisteredSubjectProperty property, object? index)? FindParentProperty(
        RegisteredSubject subject)
    {
        if (subject.Parents.Length > 0)
        {
            var parent = subject.Parents[0];
            return (parent.Property, parent.Index);
        }

        return null;
    }
}

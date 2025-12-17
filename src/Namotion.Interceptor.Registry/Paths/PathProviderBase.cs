using System.Text;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Base implementation with configurable separators and [Children] support.
/// </summary>
public abstract class PathProviderBase : IPathProvider
{
    /// <summary>
    /// Gets the character used to separate path segments.
    /// </summary>
    public virtual char PathSeparator => '.';

    /// <summary>
    /// Gets the character used to open an index bracket.
    /// </summary>
    public virtual char IndexOpen => '[';

    /// <summary>
    /// Gets the character used to close an index bracket.
    /// </summary>
    public virtual char IndexClose => ']';

    /// <inheritdoc />
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

    /// <inheritdoc />
    /// <remarks>
    /// Default implementation returns the property's BrowseName.
    /// Override to return null for no-mapping scenarios.
    /// </remarks>
    public virtual string? TryGetPropertySegment(RegisteredSubjectProperty property)
        => property.BrowseName;

    /// <inheritdoc />
    /// <remarks>
    /// Default implementation:
    /// 1. First looks up direct properties by matching TryGetPropertySegment
    /// 2. Falls back to [Children] dictionary lookup if no direct match found
    /// </remarks>
    public virtual RegisteredSubjectProperty? TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment)
    {
        // 1. Direct property lookup by segment name
        foreach (var property in subject.Properties)
        {
            if (TryGetPropertySegment(property) == segment)
            {
                return property;
            }
        }

        // 2. [Children] fallback - segment is a dictionary key
        var childrenPropertyName = ChildrenAttributeCache.GetChildrenPropertyName(subject.Subject.GetType());
        if (childrenPropertyName is not null)
        {
            // Return the Children property - caller uses segment as dictionary key
            return subject.TryGetProperty(childrenPropertyName);
        }

        return null;
    }

    /// <summary>
    /// Builds a full path from a sequence of properties and their optional indices.
    /// </summary>
    /// <param name="properties">The properties in the path with their indices.</param>
    /// <returns>The full path string.</returns>
    internal string BuildFullPath(IEnumerable<(RegisteredSubjectProperty property, object? index)> properties)
    {
        var stringBuilder = new StringBuilder();
        foreach (var (property, index) in properties)
        {
            if (stringBuilder.Length > 0)
            {
                stringBuilder.Append(PathSeparator);
            }

            var segment = TryGetPropertySegment(property);
            if (segment is not null)
            {
                stringBuilder.Append(segment);
            }

            if (index is not null)
            {
                stringBuilder.Append(IndexOpen).Append(index).Append(IndexClose);
            }
        }
        return stringBuilder.ToString();
    }

    /// <summary>
    /// Parses a full path into segments with their optional indices.
    /// </summary>
    /// <param name="path">The full path to parse.</param>
    /// <returns>An enumerable of segments and their indices.</returns>
    internal IEnumerable<(string segment, object? index)> ParseFullPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (var part in path.Split(PathSeparator))
        {
            var bracketIndex = part.IndexOf(IndexOpen);
            if (bracketIndex < 0)
            {
                yield return (part, null);
            }
            else
            {
                var name = part[..bracketIndex];
                var closeIndex = part.IndexOf(IndexClose);
                var indexString = part[(bracketIndex + 1)..closeIndex];
                object index = int.TryParse(indexString, out var intValue) ? intValue : indexString;
                yield return (name, index);
            }
        }
    }
}

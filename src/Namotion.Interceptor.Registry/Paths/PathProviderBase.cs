using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

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
        var childrenPropertyName = ChildrenAttribute.GetChildrenPropertyName(subject.Subject.GetType());
        if (childrenPropertyName is not null)
        {
            // Return the Children property - caller uses segment as dictionary key
            return subject.TryGetProperty(childrenPropertyName);
        }

        return null;
    }
}

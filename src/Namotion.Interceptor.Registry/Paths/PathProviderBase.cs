using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Base implementation with configurable separators and [InlinePaths] support.
/// </summary>
public abstract class PathProviderBase : IPathProvider
{
    /// <summary>
    /// Gets the character used to separate path segments. The separator and the two index characters must be three
    /// different characters, none of them <c>'</c>, which quotes keys; paths are ambiguous otherwise.
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

    internal PathCharacters GetCharacters() => new(PathSeparator, IndexOpen, IndexClose);

    /// <inheritdoc />
    public virtual bool IsPropertyIncluded(RegisteredSubjectProperty property) => true;

    /// <summary>
    /// Gets whether paths may pass the property: <see cref="IsPropertyIncluded"/> includes it and, for an attribute,
    /// the property it belongs to, recursively up to a property that is no attribute. The properties holding its
    /// subject are not checked.
    /// </summary>
    /// <param name="property">The property to check.</param>
    /// <returns>True when paths may pass the property; otherwise, false.</returns>
    public bool IsPropertyIncludedInPaths(RegisteredSubjectProperty property)
        => IsPropertyIncluded(property) && (!property.IsAttribute || IsAttributedPropertyIncluded(property));

    private bool IsAttributedPropertyIncluded(RegisteredSubjectProperty attribute)
    {
        var property = attribute;
        do
        {
            // An attribute whose property is missing belongs to no included property.
            property = property.Parent.TryGetProperty(property.AttributeMetadata.PropertyName);
            if (property is null || !IsPropertyIncluded(property))
            {
                return false;
            }
        }
        while (property.IsAttribute);

        return true;
    }

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
    /// 2. Falls back to [InlinePaths] dictionary lookup if no direct match found
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

        // 2. [InlinePaths] fallback - segment is a dictionary key
        var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(subject.Subject.GetType());
        if (inlinePathsPropertyName is not null)
        {
            // Return the InlinePaths property - caller uses segment as dictionary key
            return subject.TryGetProperty(inlinePathsPropertyName);
        }

        return null;
    }
}

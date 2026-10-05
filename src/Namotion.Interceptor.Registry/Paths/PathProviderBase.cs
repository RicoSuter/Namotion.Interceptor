using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Base implementation with configurable separators and [InlinePaths] support.
/// </summary>
public abstract class PathProviderBase : IPathProvider
{
    private bool _charactersValidated;

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

    /// <summary>
    /// Gets the separator and index characters, throwing when they are not distinct. Checked at first use rather
    /// than in the constructor because derived constructors may assign them after this one runs.
    /// </summary>
    internal PathCharacters GetCharacters()
    {
        if (!_charactersValidated)
        {
            PathSyntax.ValidateCharacters(this);
            _charactersValidated = true;
        }

        return new PathCharacters(PathSeparator, IndexOpen, IndexClose);
    }

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

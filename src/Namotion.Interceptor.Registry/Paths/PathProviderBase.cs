using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Base implementation with configurable separators and [InlinePaths] support. It includes every property under its
/// BrowseName unless <see cref="TryGetPropertySegment"/> is overridden.
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
    /// <remarks>
    /// The default includes every property under its BrowseName. A provider migrating from the separate
    /// <c>IsPropertyIncluded</c> and nullable <c>TryGetPropertySegment</c> overrides merges both into this method,
    /// returning false where <c>IsPropertyIncluded</c> returned false, and deletes its <c>IsPropertyIncluded</c>
    /// method: an instance method of that name hides <see cref="PathProviderExtensions.IsPropertyIncluded"/>.
    /// </remarks>
    public virtual bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.BrowseName;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The default matches the segments of <see cref="TryGetPropertySegment"/> and, when none matches, falls back to
    /// the [InlinePaths] property of the subject when that is included.
    /// </remarks>
    public virtual bool TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
    {
        foreach (var candidate in subject.Properties)
        {
            if (TryGetPropertySegment(candidate, out var candidateSegment) && candidateSegment == segment)
            {
                property = candidate;
                return true;
            }
        }

        // The segment is an [InlinePaths] dictionary key; the caller uses it as the key.
        var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(subject.Subject.GetType());
        if (inlinePathsPropertyName is not null &&
            subject.TryGetProperty(inlinePathsPropertyName) is { } inlinePathsProperty &&
            TryGetPropertySegment(inlinePathsProperty, out _))
        {
            property = inlinePathsProperty;
            return true;
        }

        property = null;
        return false;
    }
}

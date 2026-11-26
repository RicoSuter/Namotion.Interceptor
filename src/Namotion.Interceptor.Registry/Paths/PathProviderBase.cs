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

    /// <summary>
    /// Gets a number that increases after every change of the answers of <see cref="TryGetPropertySegment"/> and
    /// <see cref="TryGetPropertyFromSegment"/>, and never decreases. 0, the default, means the answers never change.
    /// </summary>
    /// <remarks>
    /// A provider whose answers change while it is in use overrides it and raises it after each change: caches of paths
    /// serve an entry only while it is unchanged.
    /// </remarks>
    public virtual long Version => 0;

    internal PathCharacters GetCharacters() => new(PathSeparator, IndexOpen, IndexClose);

    /// <inheritdoc />
    /// <remarks>
    /// The default includes every property under its BrowseName. An <c>IsPropertyIncluded</c> method on a derived
    /// provider hides <see cref="PathProviderExtensions.IsPropertyIncluded"/>; delete it and return false here instead.
    /// </remarks>
    public virtual bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.BrowseName;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The default matches the segments of <see cref="TryGetPropertySegment"/> of every property other than the
    /// [InlinePaths] property and, when none matches, falls back to the [InlinePaths] property of the subject when that
    /// is included.
    /// </remarks>
    public virtual bool TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
    {
        foreach (var candidate in subject.Properties)
        {
            // The [InlinePaths] property yields its own segment to every other property, so it is matched only below.
            if (TryGetPropertySegment(candidate, out var candidateSegment) && candidateSegment == segment &&
                !candidate.IsInlinePathsProperty)
            {
                property = candidate;
                return true;
            }
        }

        // The segment is the [InlinePaths] property's own segment or a dictionary key; the caller tells them apart.
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

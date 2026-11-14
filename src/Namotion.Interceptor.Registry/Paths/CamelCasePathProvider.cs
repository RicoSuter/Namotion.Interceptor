using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Decorator that transforms segment names to/from camelCase.
/// Implements IPathProvider to wrap any other IPathProvider.
/// </summary>
public class CamelCasePathProvider : IPathProvider
{
    private readonly IPathProvider _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CamelCasePathProvider"/> class.
    /// </summary>
    /// <param name="inner">The inner path provider to wrap.</param>
    public CamelCasePathProvider(IPathProvider inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets a singleton instance that wraps the <see cref="DefaultPathProvider"/>.
    /// </summary>
    public static CamelCasePathProvider Instance { get; } = new(DefaultPathProvider.Instance);

    /// <inheritdoc />
    /// <remarks>
    /// Includes the properties the inner provider includes, with its segment transformed to camelCase.
    /// </remarks>
    public bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        if (!_inner.TryGetPropertySegment(property, out var innerSegment))
        {
            segment = null;
            return false;
        }

        segment = ToCamelCase(innerSegment);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Converts the segment from camelCase to PascalCase before looking it up in the inner provider.
    /// </remarks>
    public bool TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment, [NotNullWhen(true)] out RegisteredSubjectProperty? property)
        => _inner.TryGetPropertyFromSegment(subject, ToPascalCase(segment), out property);

    private static string ToCamelCase(string value) =>
        value.Length > 1 ? char.ToLowerInvariant(value[0]) + value[1..] : value.ToLowerInvariant();

    private static string ToPascalCase(string value) =>
        value.Length > 1 ? char.ToUpperInvariant(value[0]) + value[1..] : value.ToUpperInvariant();
}

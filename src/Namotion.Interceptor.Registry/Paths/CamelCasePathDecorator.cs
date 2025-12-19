using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Decorator that transforms segment names to/from camelCase.
/// Implements IPathProvider to wrap any other IPathProvider.
/// </summary>
public class CamelCasePathDecorator : IPathProvider
{
    private readonly IPathProvider _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CamelCasePathDecorator"/> class.
    /// </summary>
    /// <param name="inner">The inner path provider to wrap.</param>
    public CamelCasePathDecorator(IPathProvider inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets a singleton instance that wraps the <see cref="DefaultPathProvider"/>.
    /// </summary>
    public static CamelCasePathDecorator Instance { get; } = new(DefaultPathProvider.Instance);

    /// <inheritdoc />
    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => _inner.IsPropertyIncluded(property);

    /// <inheritdoc />
    /// <remarks>
    /// Transforms the segment from the inner provider to camelCase.
    /// Returns null if the inner provider returns null.
    /// </remarks>
    public string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var segment = _inner.TryGetPropertySegment(property);
        return segment is not null ? ToCamelCase(segment) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Converts the segment from camelCase to PascalCase before looking up
    /// in the inner provider.
    /// </remarks>
    public RegisteredSubjectProperty? TryGetPropertyFromSegment(
        RegisteredSubject subject, string segment)
        => _inner.TryGetPropertyFromSegment(subject, ToPascalCase(segment));

    private static string ToCamelCase(string value) =>
        value.Length > 1 ? char.ToLowerInvariant(value[0]) + value[1..] : value.ToLowerInvariant();

    private static string ToPascalCase(string value) =>
        value.Length > 1 ? char.ToUpperInvariant(value[0]) + value[1..] : value.ToUpperInvariant();
}

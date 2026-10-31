using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Default path provider that uses property BrowseNames as path segments.
/// </summary>
public class DefaultPathProvider : PathProviderBase
{
    private readonly char _pathSeparator;

    /// <summary>
    /// Gets the singleton instance of the default path provider (separator '.').
    /// </summary>
    public static DefaultPathProvider Instance { get; } = new();

    private DefaultPathProvider()
        : this('.')
    {
    }

    /// <summary>Creates a default path provider with the given separator.</summary>
    public DefaultPathProvider(char pathSeparator)
    {
        _pathSeparator = pathSeparator;
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    protected override bool TryGetIncludedPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        // A derived provider may override the members this skips. Checked per call rather than cached in a field: a
        // devirtualized call folds the check and its fallback away, so the override inlines.
        if (GetType() != typeof(DefaultPathProvider))
        {
            return base.TryGetIncludedPropertySegment(property, out segment);
        }

        segment = property.BrowseName;
        return true;
    }
}

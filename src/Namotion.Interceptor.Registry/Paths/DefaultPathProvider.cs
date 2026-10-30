using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Default path provider that uses property BrowseNames as path segments.
/// </summary>
public class DefaultPathProvider : PathProviderBase
{
    private readonly char _pathSeparator;
    private readonly bool _isExactType;

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
        _isExactType = GetType() == typeof(DefaultPathProvider);
    }

    /// <inheritdoc />
    public override char PathSeparator => _pathSeparator;

    /// <inheritdoc />
    public override bool TryGetIncludedPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        // A derived provider may override the members this skips.
        if (!_isExactType)
        {
            return base.TryGetIncludedPropertySegment(property, out segment);
        }

        segment = property.BrowseName;
        return true;
    }
}

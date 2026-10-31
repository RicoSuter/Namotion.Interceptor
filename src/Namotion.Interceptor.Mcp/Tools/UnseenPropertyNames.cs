using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The requested property names that no observed subject has as an exposed property yet, so a tool can warn
/// about a name that resolves on no subject in scope instead of silently matching or returning nothing. A
/// <c>where</c> path is resolved like the filter resolves it, a <c>properties</c> name like the selection matches it,
/// which is a scalar property only.
/// </summary>
internal sealed class UnseenPropertyNames
{
    private readonly List<string> _wherePaths;
    private readonly List<string> _selectedNames;

    private UnseenPropertyNames(List<string> wherePaths, List<string> selectedNames)
    {
        _wherePaths = wherePaths;
        _selectedNames = selectedNames;
    }

    /// <summary>
    /// Creates a tracker for the names of both sources, each without duplicates, or returns null when there are none.
    /// </summary>
    public static UnseenPropertyNames? TryCreate(
        IReadOnlyCollection<string>? wherePaths, IReadOnlyCollection<string>? selectedNames) =>
        wherePaths is { Count: > 0 } || selectedNames is { Count: > 0 }
            ? new UnseenPropertyNames([.. wherePaths ?? []], [.. selectedNames ?? []])
            : null;

    /// <summary>
    /// Marks as seen every <c>where</c> path that leads from <paramref name="subject"/> to a property the path provider
    /// exposes on a shown subject, also one that holds subjects but not an attribute, and every selected name that is a
    /// scalar property of it.
    /// </summary>
    public void Observe(RegisteredSubject subject, PathProviderBase pathProvider, McpVisibility visibility)
    {
        for (var index = _wherePaths.Count - 1; index >= 0; index--)
        {
            if (McpToolHelper.TryGetFilterPropertyFromPath(subject, pathProvider, _wherePaths[index], visibility) is not null)
            {
                _wherePaths.RemoveAt(index);
            }
        }

        if (_selectedNames.Count == 0)
        {
            return;
        }

        foreach (var property in subject.Properties)
        {
            if (McpToolHelper.TryGetSelectableSegment(property, pathProvider) is { } segment)
            {
                _selectedNames.Remove(segment);
            }
        }
    }

    /// <summary>
    /// One warning per name no observed subject has, or null when every name was seen.
    /// </summary>
    public IReadOnlyList<string>? CreateWarnings() =>
        _wherePaths.Count > 0 || _selectedNames.Count > 0
            ? _wherePaths.Concat(_selectedNames)
                .Distinct(StringComparer.Ordinal)
                .Select(name => $"No subject in scope has a property named '{name}'.")
                .ToArray()
            : null;
}

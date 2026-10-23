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
    /// Marks every name that resolves to a scalar property of <paramref name="subject"/> as seen.
    /// </summary>
    public void Observe(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        for (var index = _wherePaths.Count - 1; index >= 0; index--)
        {
            if (McpToolHelper.TryGetFilterPropertyFromPath(subject, pathProvider, _wherePaths[index]) is not null)
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

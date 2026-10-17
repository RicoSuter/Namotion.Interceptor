using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The requested property names that no observed subject has as an exposed scalar property yet, so a tool can warn
/// about a name that resolves on no subject in scope instead of silently matching or returning nothing. A
/// <c>where</c> name is resolved like the filter resolves it, a <c>properties</c> name like the selection matches it.
/// </summary>
internal sealed class UnseenPropertyNames
{
    private readonly List<string> _whereNames;
    private readonly List<string> _selectedNames;

    private UnseenPropertyNames(List<string> whereNames, List<string> selectedNames)
    {
        _whereNames = whereNames;
        _selectedNames = selectedNames;
    }

    /// <summary>
    /// Creates a tracker for the names of both sources, each without duplicates, or returns null when there are none.
    /// </summary>
    public static UnseenPropertyNames? TryCreate(
        IReadOnlyCollection<string>? whereNames, IReadOnlyCollection<string>? selectedNames) =>
        whereNames is { Count: > 0 } || selectedNames is { Count: > 0 }
            ? new UnseenPropertyNames([.. whereNames ?? []], [.. selectedNames ?? []])
            : null;

    /// <summary>
    /// Marks every name that resolves to a scalar property of <paramref name="subject"/> as seen.
    /// </summary>
    public void Observe(RegisteredSubject subject, PathProviderBase pathProvider)
    {
        for (var index = _whereNames.Count - 1; index >= 0; index--)
        {
            if (McpToolHelper.TryGetScalarProperty(subject, pathProvider, _whereNames[index]) is not null)
            {
                _whereNames.RemoveAt(index);
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
        _whereNames.Count > 0 || _selectedNames.Count > 0
            ? _whereNames.Concat(_selectedNames)
                .Distinct(StringComparer.Ordinal)
                .Select(name => $"No subject in scope has a property named '{name}'.")
                .ToArray()
            : null;
}

using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// The rows of the Discover page, ordered by type and then by name so the list moves little while results arrive, a
/// line for each discoverer that failed, and which rows show their included items. Not thread-safe: the page changes
/// it on the renderer's dispatcher.
/// </summary>
internal sealed class SubjectDiscoveryList
{
    private static readonly Comparer<SubjectDiscoveryRow> RowComparer = Comparer<SubjectDiscoveryRow>.Create(CompareRows);

    private readonly List<SubjectDiscoveryRow> _rows = [];
    private readonly List<string> _failures = [];
    private readonly HashSet<(Type SubjectType, string Key)> _expandedRows = [];

    public IReadOnlyList<SubjectDiscoveryRow> Rows => _rows;

    public IReadOnlyList<string> Failures => _failures;

    public void Add(DiscoveredSubject discovered, SubjectTypeDescriptor descriptor)
    {
        var row = new SubjectDiscoveryRow(discovered, descriptor);
        var index = _rows.BinarySearch(row, RowComparer);
        _rows.Insert(index < 0 ? ~index : index, row);
    }

    public void AddFailure(SubjectTypeDescriptor descriptor)
    {
        _failures.Add($"{descriptor.DisplayName}: search failed");
    }

    /// <summary>
    /// Whether the row shows its included items. A row is collapsed until it is toggled, and stays as toggled while
    /// further results arrive, until the list is cleared.
    /// </summary>
    public bool IsExpanded(SubjectDiscoveryRow row) =>
        _expandedRows.Contains((row.Discovered.SubjectType, row.Discovered.Result.Key));

    public void ToggleExpanded(SubjectDiscoveryRow row)
    {
        var key = (row.Discovered.SubjectType, row.Discovered.Result.Key);
        if (!_expandedRows.Remove(key))
        {
            _expandedRows.Add(key);
        }
    }

    public void Clear()
    {
        _rows.Clear();
        _failures.Clear();
        _expandedRows.Clear();
    }

    /// <summary>
    /// The accessible name of the control that shows or hides the included items of a row.
    /// </summary>
    public static string GetExpanderLabel(int itemCount, bool isExpanded)
    {
        var action = isExpanded ? "Hide" : "Show";
        return itemCount == 1 ? $"{action} 1 included item" : $"{action} {itemCount} included items";
    }

    private static int CompareRows(SubjectDiscoveryRow first, SubjectDiscoveryRow second)
    {
        var byType = StringComparer.OrdinalIgnoreCase.Compare(first.Descriptor.DisplayName, second.Descriptor.DisplayName);
        if (byType != 0)
        {
            return byType;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(first.Discovered.Result.DisplayName, second.Discovered.Result.DisplayName);
        if (byName != 0)
        {
            return byName;
        }

        var byKey = StringComparer.Ordinal.Compare(first.Discovered.Result.Key, second.Discovered.Result.Key);
        return byKey != 0
            ? byKey
            : StringComparer.Ordinal.Compare(first.Descriptor.Type.FullName, second.Descriptor.Type.FullName);
    }
}

/// <summary>
/// A found device with the description of its subject type.
/// </summary>
internal sealed record SubjectDiscoveryRow(DiscoveredSubject Discovered, SubjectTypeDescriptor Descriptor);

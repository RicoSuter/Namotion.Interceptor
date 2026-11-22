using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// The rows of the Discover page, the devices that are not added yet first and each part ordered by type and then by
/// name so the list moves little while results arrive, a line for each discoverer that failed, and which rows show
/// their included items. Not thread-safe: the page changes it on the renderer's dispatcher.
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
        Insert(new SubjectDiscoveryRow(discovered, descriptor));
    }

    /// <summary>
    /// Marks the row of the device, found by its subject type and key, as added, with <paramref name="existingSubject"/>
    /// as the subject that represents it, and moves it to its place among the added devices. Does nothing when the
    /// list has no such row.
    /// </summary>
    public void MarkAdded(DiscoveredSubject discovered, IInterceptorSubject existingSubject)
    {
        var index = _rows.FindIndex(row =>
            row.Discovered.SubjectType == discovered.SubjectType &&
            string.Equals(row.Discovered.Result.Key, discovered.Result.Key, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var row = _rows[index];
        _rows.RemoveAt(index);
        Insert(row with { Discovered = row.Discovered with { ExistingSubject = existingSubject } });
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

    private void Insert(SubjectDiscoveryRow row)
    {
        var index = _rows.BinarySearch(row, RowComparer);
        _rows.Insert(index < 0 ? ~index : index, row);
    }

    private static int CompareRows(SubjectDiscoveryRow first, SubjectDiscoveryRow second)
    {
        var byExisting = (first.Discovered.ExistingSubject is not null).CompareTo(second.Discovered.ExistingSubject is not null);
        if (byExisting != 0)
        {
            return byExisting;
        }

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

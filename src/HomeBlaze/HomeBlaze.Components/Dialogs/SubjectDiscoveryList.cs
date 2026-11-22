using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// The rows of the Discover page, ordered by type and then by name so the list moves little while results arrive,
/// and a line for each discoverer that failed. Not thread-safe: the page changes it on the renderer's dispatcher.
/// </summary>
internal sealed class SubjectDiscoveryList
{
    private static readonly Comparer<SubjectDiscoveryRow> RowComparer = Comparer<SubjectDiscoveryRow>.Create(CompareRows);

    private readonly List<SubjectDiscoveryRow> _rows = [];
    private readonly List<string> _failures = [];

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

    public void Clear()
    {
        _rows.Clear();
        _failures.Clear();
    }

    private static int CompareRows(SubjectDiscoveryRow? first, SubjectDiscoveryRow? second)
    {
        if (first is null || second is null)
        {
            return first is null ? (second is null ? 0 : -1) : 1;
        }

        var byType = StringComparer.OrdinalIgnoreCase.Compare(first.Descriptor.DisplayName, second.Descriptor.DisplayName);
        if (byType != 0)
        {
            return byType;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(first.Discovered.Result.DisplayName, second.Discovered.Result.DisplayName);
        return byName != 0
            ? byName
            : StringComparer.Ordinal.Compare(first.Discovered.Result.Key, second.Discovered.Result.Key);
    }
}

/// <summary>
/// A found device with the description of its subject type.
/// </summary>
internal sealed record SubjectDiscoveryRow(DiscoveredSubject Discovered, SubjectTypeDescriptor Descriptor);

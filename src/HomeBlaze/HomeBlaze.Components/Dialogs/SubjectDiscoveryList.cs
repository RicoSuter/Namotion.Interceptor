using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Components.Dialogs;

/// <summary>
/// The rows of the Discover page, the devices that are not added yet first and each part ordered by type and then by
/// name so the list moves little while results arrive, and a line for each subject discovery that failed. Not
/// thread-safe: the page changes it on the renderer's dispatcher.
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
        Insert(new SubjectDiscoveryRow(discovered, descriptor));
    }

    /// <summary>
    /// Marks the rows that <paramref name="createdSubject"/> represents as added, with it as their subject, and moves
    /// them to their place among the added devices. These are the row of <paramref name="picked"/>, found by its
    /// subject type and key, and every row not added yet whose subject type <paramref name="createdSubject"/> is an
    /// instance of and whose result matches it. A result whose <see cref="IDiscoveryResult.Matches"/> throws is logged
    /// and does not match.
    /// </summary>
    public void MarkAdded(DiscoveredSubject picked, IInterceptorSubject createdSubject, ILogger logger)
    {
        var isChanged = false;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];

            // The picked row is marked also when the subject was configured to differ from the found device.
            var isPicked =
                row.Discovered.SubjectType == picked.SubjectType &&
                string.Equals(row.Discovered.Result.Key, picked.Result.Key, StringComparison.Ordinal);
            if (isPicked ||
                (row.Discovered.ExistingSubject is null &&
                 row.Discovered.SubjectType.IsInstanceOfType(createdSubject) &&
                 Matches(row.Discovered.Result, createdSubject, logger)))
            {
                _rows[index] = row with { Discovered = row.Discovered with { ExistingSubject = createdSubject } };
                isChanged = true;
            }
        }

        if (isChanged)
        {
            _rows.Sort(RowComparer);
        }
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

    private static bool Matches(IDiscoveryResult result, IInterceptorSubject subject, ILogger logger)
    {
        try
        {
            return result.Matches(subject);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The found device {Key} failed to match the created subject.", result.Key);
            return false;
        }
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

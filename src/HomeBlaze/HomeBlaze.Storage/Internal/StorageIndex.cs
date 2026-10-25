using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The entries of a storage by exact path.
/// </summary>
/// <remarks>Not thread-safe. Only the <see cref="StorageWorker"/> reads or writes it.</remarks>
internal sealed class StorageIndex
{
    private readonly Dictionary<string, StorageEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// True when an entry was added, replaced or removed since <see cref="MarkApplied"/>, and for a new index:
    /// the tree can still hold the subjects of an earlier connection.
    /// </summary>
    /// <remarks>
    /// Not set by a change of an entry itself. An entry that gets another subject is replaced with <see cref="Set"/>.
    /// </remarks>
    public bool HasUnappliedChanges { get; private set; } = true;

    public IReadOnlyCollection<StorageEntry> Entries => _entries.Values;

    public bool TryGet(string path, [MaybeNullWhen(false)] out StorageEntry entry)
        => _entries.TryGetValue(path, out entry);

    public bool TryGetEntry(IInterceptorSubject subject, [MaybeNullWhen(false)] out StorageEntry entry)
    {
        foreach (var candidate in _entries.Values)
        {
            if (ReferenceEquals(candidate.Subject, subject))
            {
                entry = candidate;
                return true;
            }
        }

        entry = null;
        return false;
    }

    public void Set(StorageEntry entry)
    {
        _entries[entry.Path] = entry;
        HasUnappliedChanges = true;
    }

    public bool Remove(string path)
    {
        HasUnappliedChanges = true;
        return _entries.Remove(path);
    }

    /// <summary>
    /// Records that the tree was assigned from the entries as they are now.
    /// </summary>
    public void MarkApplied()
        => HasUnappliedChanges = false;

    /// <summary>
    /// Adds a folder entry for every ancestor of the path that has none.
    /// </summary>
    public void EnsureFolders(string path)
    {
        for (var parent = StoragePath.GetParent(path);
             parent.Length > 0 && !_entries.ContainsKey(parent);
             parent = StoragePath.GetParent(parent))
        {
            Set(new StorageEntry { Path = parent, IsFolder = true });
        }
    }
}

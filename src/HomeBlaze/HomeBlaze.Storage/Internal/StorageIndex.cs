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

    public int Count => _entries.Count;

    public IReadOnlyCollection<StorageEntry> Entries => _entries.Values;

    public bool TryGet(string path, [MaybeNullWhen(false)] out StorageEntry entry)
        => _entries.TryGetValue(path, out entry);

    public bool TryGetPath(IInterceptorSubject subject, [MaybeNullWhen(false)] out string path)
    {
        // Scanned on purpose: a second dictionary would have to follow every change of an entry's subject.
        foreach (var entry in _entries.Values)
        {
            if (ReferenceEquals(entry.Subject, subject))
            {
                path = entry.Path;
                return true;
            }
        }

        path = null;
        return false;
    }

    public void Set(StorageEntry entry)
        => _entries[entry.Path] = entry;

    public bool Remove(string path)
        => _entries.Remove(path);

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

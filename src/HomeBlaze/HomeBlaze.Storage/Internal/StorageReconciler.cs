using System.Text;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Brings the subject tree of a storage in line with the storage itself. A pass lists the storage once,
/// compares the listing with the index of what was applied last, loads what is new or changed, and assigns
/// the children of each changed folder once.
/// </summary>
/// <remarks>Not thread-safe. Every member runs on the <see cref="StorageWorker"/>.</remarks>
internal sealed class StorageReconciler
{
    private readonly IBlobStorage _client;
    private readonly FluentStorageContainer _storage;
    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly StorageIndex _index;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    // Cancelled when the connection ends. Nothing is assigned to the tree after that.
    private readonly CancellationToken _connectionToken;

    // True while a pass runs, and after one that ended before it applied its result.
    private bool _hasUnfinishedPass;

    public StorageReconciler(
        IBlobStorage client,
        FluentStorageContainer storage,
        FileSubjectFactory subjectFactory,
        ConfigurableSubjectSerializer serializer,
        StorageIndex index,
        TimeProvider timeProvider,
        ILogger? logger,
        CancellationToken connectionToken)
    {
        _client = client;
        _storage = storage;
        _subjectFactory = subjectFactory;
        _serializer = serializer;
        _index = index;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectionToken = connectionToken;
    }

    /// <summary>
    /// Runs one pass.
    /// </summary>
    /// <param name="namedPaths">Paths an event named. They are compared by content even when their version is unchanged.</param>
    /// <param name="allNamed">Treats every file as named, for when events were lost.</param>
    /// <param name="cancellationToken">Cancels the pass. It ends at its next storage call or between two files.</param>
    /// <exception cref="StorageUnresponsiveException">
    /// The storage did not answer in time. The pass ends without applying anything, and the next pass does the
    /// work again.
    /// </exception>
    public async Task ReconcileAsync(IReadOnlySet<string> namedPaths, bool allNamed, CancellationToken cancellationToken)
    {
        // A pass that ended early took with it which paths were named and whether it moved a subject. The pass
        // after it therefore compares every file, and applies as if a subject had moved.
        var followsUnfinishedPass = _hasUnfinishedPass;
        var isEveryFileNamed = allNamed || followsUnfinishedPass;
        _hasUnfinishedPass = true;

        var listing = await ListAsync(cancellationToken);
        var movableEntries = RemoveMissingEntries(listing);

        var hasMovedSubjects = followsUnfinishedPass;
        foreach (var listed in listing.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (listed.IsFolder)
            {
                if (!_index.TryGet(listed.Path, out _))
                {
                    _index.Set(new StorageEntry { Path = listed.Path, IsFolder = true });
                }

                continue;
            }

            try
            {
                var isNamed = isEveryFileNamed || namedPaths.Contains(listed.Path);
                if (!_index.TryGet(listed.Path, out var entry) || NeedsLoad(entry, listed, isNamed))
                {
                    hasMovedSubjects |= await AddAsync(listed, movableEntries, cancellationToken);
                }
                else if (entry.Subject != null && (entry.Version != listed.Version || isNamed))
                {
                    await RefreshAsync(entry, listed, cancellationToken);
                }
            }
            // Filtered by the token of the pass, not by the kind of exception: subject code that times out throws
            // an OperationCanceledException of its own, which is a failed load like any other. An unresponsive
            // storage ends the pass instead: recorded as failed, the file would not be loaded again until it
            // changes, and so would every file after it.
            catch (Exception exception) when (exception is not StorageUnresponsiveException && !cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(exception, "Failed to load: {Path}", listed.Path);
                RecordFailure(listed);
            }
        }

        Apply(hasMovedSubjects);
        _hasUnfinishedPass = false;
    }

    /// <summary>
    /// Builds the children of every folder from the index and assigns those that changed.
    /// </summary>
    /// <param name="keepRemovedUntilAdded">
    /// Assigns in two steps: first everything that is added, then everything that is removed. Needed when a
    /// subject moved, because an assignment detaches before it attaches and would stop and restart the subject.
    /// </param>
    public void Apply(bool keepRemovedUntilAdded = false)
    {
        var childrenByFolder = new Dictionary<string, Dictionary<string, IInterceptorSubject>>(StringComparer.Ordinal)
        {
            [StoragePath.Root] = new()
        };

        // Placed entries go first, so one that is in the tree never loses its key to a newcomer.
        // Within each group a folder comes before what is inside it.
        var candidates = _index.Entries
            .Where(entry => entry.IsFolder || entry.Subject != null)
            .OrderBy(entry => entry.State != StorageEntryState.Placed)
            .ThenBy(entry => StoragePath.GetDepth(entry.Path))
            .ThenBy(entry => !entry.IsFolder)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in candidates)
        {
            var key = entry.IsFolder ? StoragePath.GetName(entry.Path) : GetChildKey(entry.Path, entry.Subject!);
            if (!childrenByFolder.TryGetValue(StoragePath.GetParent(entry.Path), out var siblings) || siblings.ContainsKey(key))
            {
                if (entry.State != StorageEntryState.KeyTaken)
                {
                    _logger?.LogWarning("Skipping '{Path}': key \"{Key}\" is taken or its folder is not placed", entry.Path, key);
                }

                entry.State = StorageEntryState.KeyTaken;
                entry.Key = key;
                entry.Subject = null;
                continue;
            }

            if (entry.IsFolder)
            {
                entry.Subject ??= new VirtualFolder(_storage, entry.Path + "/");
                childrenByFolder[entry.Path] = new Dictionary<string, IInterceptorSubject>();
            }

            siblings.Add(key, entry.Subject!);
            entry.State = StorageEntryState.Placed;
            entry.Key = key;
        }

        // Deepest first, the root last: a new folder is attached with its children already in place.
        var folders = childrenByFolder.Keys
            .OrderByDescending(path => path.Length == 0 ? -1 : StoragePath.GetDepth(path))
            .ToList();

        // The connection that follows an ended one owns the tree.
        _connectionToken.ThrowIfCancellationRequested();

        // Without the flow of this item, work that a lifecycle handler starts here queues its calls into the storage instead of running them inline.
        using (ExecutionContext.SuppressFlow())
        {
            if (keepRemovedUntilAdded)
            {
                foreach (var folder in folders)
                {
                    var union = new Dictionary<string, IInterceptorSubject>(GetChildren(folder));
                    foreach (var (key, subject) in childrenByFolder[folder])
                    {
                        union[key] = subject;
                    }

                    AssignChildren(folder, union);
                }
            }

            foreach (var folder in folders)
            {
                AssignChildren(folder, childrenByFolder[folder]);
            }
        }
    }

    /// <summary>
    /// Checks whether a new subject at the path could be placed: its folder is placed or can be created, and
    /// its key in that folder is free.
    /// </summary>
    public bool IsKeyFree(string path, IInterceptorSubject subject)
    {
        var key = GetChildKey(path, subject);
        var parent = StoragePath.GetParent(path);

        // A folder that does not exist yet is created with the file, so its own name has to be free where it starts.
        while (parent.Length > 0 && !_index.TryGet(parent, out _))
        {
            key = StoragePath.GetName(parent);
            parent = StoragePath.GetParent(parent);
        }

        return IsKeyFree(parent, key);
    }

    public async Task<StorageVersion> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        var blobs = await _client.GetBlobsAsync([path], cancellationToken);
        var blob = blobs.FirstOrDefault();
        return blob == null ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
    }

    /// <summary>
    /// Hashes the file without holding its content in memory.
    /// </summary>
    public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(path, cancellationToken);
        return await StorageHash.ComputeAsync(stream, cancellationToken)
            .WithStorageTimeoutAsync("hashing", path, _timeProvider, cancellationToken);
    }

    private static string GetChildKey(string path, IInterceptorSubject subject)
        => subject is IConfigurable && IsJson(path)
            ? Path.GetFileNameWithoutExtension(path)
            : StoragePath.GetName(path);

    private static bool IsJson(string path)
        => Path.GetExtension(path).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);

    private bool IsKeyFree(string parent, string key)
    {
        if (parent.Length > 0 &&
            !(_index.TryGet(parent, out var folder) && folder is { IsFolder: true, State: StorageEntryState.Placed }))
        {
            return false;
        }

        foreach (var entry in _index.Entries)
        {
            if (entry.State == StorageEntryState.Placed && entry.Key == key && StoragePath.GetParent(entry.Path) == parent)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, StorageListing>> ListAsync(CancellationToken cancellationToken)
    {
        var blobs = await _client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        var listing = new Dictionary<string, StorageListing>(StringComparer.Ordinal);
        foreach (var blob in blobs)
        {
            var path = StoragePath.Normalize(blob.FullPath);
            if (path.Length == 0 || StoragePathFilter.IsIgnored(path))
            {
                continue;
            }

            var version = blob.IsFolder ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
            listing[path] = new StorageListing(path, blob.IsFolder, version);
        }

        // Not every backend lists the folders of its files.
        foreach (var path in listing.Keys.ToList())
        {
            for (var parent = StoragePath.GetParent(path);
                 parent.Length > 0 && !listing.ContainsKey(parent);
                 parent = StoragePath.GetParent(parent))
            {
                listing[parent] = new StorageListing(parent, true, default);
            }
        }

        return listing;
    }

    /// <summary>
    /// Removes the entries that are gone or changed kind, and returns those of them that hold a configurable
    /// subject by content hash: a new file with the same content can be the same subject under a new path.
    /// </summary>
    private Dictionary<string, List<StorageEntry>> RemoveMissingEntries(Dictionary<string, StorageListing> listing)
    {
        var movableEntries = new Dictionary<string, List<StorageEntry>>(StringComparer.Ordinal);
        foreach (var entry in _index.Entries.ToList())
        {
            if (listing.TryGetValue(entry.Path, out var listed) && listed.IsFolder == entry.IsFolder)
            {
                continue;
            }

            _index.Remove(entry.Path);

            if (entry is { IsFolder: false, Subject: IConfigurable, Hash: not null })
            {
                if (!movableEntries.TryGetValue(entry.Hash, out var entries))
                {
                    movableEntries[entry.Hash] = entries = [];
                }

                entries.Add(entry);
            }
        }

        return movableEntries;
    }

    /// <summary>
    /// Decides whether an entry without a subject is loaded again.
    /// </summary>
    private bool NeedsLoad(StorageEntry entry, StorageListing listed, bool isNamed)
    {
        if (entry.Subject != null)
        {
            return false;
        }

        if (entry.Version != listed.Version || isNamed)
        {
            return true;
        }

        return entry is { State: StorageEntryState.KeyTaken, Key: not null } &&
               IsKeyFree(StoragePath.GetParent(entry.Path), entry.Key);
    }

    /// <returns>True when an existing subject was moved to the path instead of creating one.</returns>
    private async Task<bool> AddAsync(
        StorageListing listed,
        Dictionary<string, List<StorageEntry>> movableEntries,
        CancellationToken cancellationToken)
    {
        var blob = new Blob(listed.Path) { Size = listed.Version.Size, LastModificationTime = listed.Version.Modified };
        var entry = new StorageEntry { Path = listed.Path, IsFolder = false, Version = listed.Version };
        var isMoved = false;

        if (IsJson(listed.Path))
        {
            var content = await ReadAsync(listed.Path, cancellationToken);
            entry.Hash = StorageHash.Compute(content);

            // More than one candidate cannot be told apart, so none of them is moved.
            // Only into another folder: the registry keeps the old key of a subject that is re-keyed within one dictionary.
            if (movableEntries.Remove(entry.Hash, out var candidates) && candidates.Count == 1 &&
                StoragePath.GetParent(candidates[0].Path) != StoragePath.GetParent(listed.Path))
            {
                entry.Subject = candidates[0].Subject;
                isMoved = true;
            }
            else
            {
                // A configurable subject enters the context when it is created, which runs lifecycle handlers as
                // an assignment in Apply does.
                using (ExecutionContext.SuppressFlow())
                {
                    entry.Subject = _subjectFactory.CreateFromJson(_storage, blob.FullPath, DecodeText(content));
                }

                if (entry.Subject is IStorageFile file)
                {
                    await file.OnFileChangedAsync(cancellationToken);
                }
            }
        }
        else
        {
            entry.Subject = await _subjectFactory.CreateFromBlobAsync(_storage, blob, cancellationToken)
                ?? throw new InvalidOperationException($"No subject could be created for '{listed.Path}'.");

            if (entry.Subject is not GenericFile)
            {
                var hash = await ComputeHashAsync(listed.Path, cancellationToken);

                // The subject read the file before this did. The hash of a later content would make the next pass
                // take the subject for current, so it is only recorded when the file did not change meanwhile.
                if (await GetVersionAsync(listed.Path, cancellationToken) == listed.Version)
                {
                    entry.Hash = hash;
                }
            }
        }

        _index.Set(entry);
        return isMoved;
    }

    private async Task RefreshAsync(StorageEntry entry, StorageListing listed, CancellationToken cancellationToken)
    {
        if (entry.Subject is GenericFile genericFile)
        {
            // Holds nothing but size and time, so there is no content to compare.
            if (entry.Version != listed.Version)
            {
                await genericFile.OnFileChangedAsync(cancellationToken);
                entry.Version = listed.Version;
            }

            return;
        }

        string hash;
        if (entry.Subject is IStorageFile file)
        {
            // The subject reads the file itself, so its bytes are only hashed here.
            hash = await ComputeHashAsync(listed.Path, cancellationToken);
            if (hash != entry.Hash)
            {
                await file.OnFileChangedAsync(cancellationToken);
                _logger?.LogInformation("Reloaded: {Path}", listed.Path);
            }
            else
            {
                file.FileSize = listed.Version.Size;
                file.LastModified = listed.Version.Modified?.UtcDateTime ?? file.LastModified;
            }
        }
        else
        {
            var content = await ReadAsync(listed.Path, cancellationToken);
            hash = StorageHash.Compute(content);
            if (hash != entry.Hash && entry.Subject is IConfigurable configurable)
            {
                // Can attach nested subjects, see the creation in AddAsync.
                using (ExecutionContext.SuppressFlow())
                {
                    _serializer.UpdateConfiguration(entry.Subject, DecodeText(content));
                }

                await configurable.ApplyConfigurationAsync(cancellationToken);
                _logger?.LogInformation("Reloaded: {Path}", listed.Path);
            }
        }

        entry.Hash = hash;
        entry.Version = listed.Version;
    }

    private void RecordFailure(StorageListing listed)
    {
        // A subject that is in the tree stays there with what it has. Recording the version makes the next
        // pass wait for another change instead of failing again on every pass.
        if (_index.TryGet(listed.Path, out var entry) && entry.Subject != null)
        {
            entry.Version = listed.Version;
            return;
        }

        _index.Set(new StorageEntry
        {
            Path = listed.Path,
            IsFolder = false,
            Version = listed.Version,
            State = StorageEntryState.Failed
        });
    }

    private Dictionary<string, IInterceptorSubject> GetChildren(string folder)
        => folder.Length == 0
            ? _storage.Children
            : ((VirtualFolder)GetEntry(folder).Subject!).Children;

    private void AssignChildren(string folder, Dictionary<string, IInterceptorSubject> children)
    {
        var current = GetChildren(folder);
        if (current.Count == children.Count &&
            children.All(pair => current.TryGetValue(pair.Key, out var subject) && ReferenceEquals(subject, pair.Value)))
        {
            return;
        }

        // A copy, because the union of the first step is not what the folder holds after the second.
        var assigned = new Dictionary<string, IInterceptorSubject>(children);
        if (folder.Length == 0)
        {
            _storage.Children = assigned;
        }
        else
        {
            ((VirtualFolder)GetEntry(folder).Subject!).Children = assigned;
        }
    }

    private StorageEntry GetEntry(string path)
        => _index.TryGet(path, out var entry)
            ? entry
            : throw new InvalidOperationException($"No entry for '{path}'.");

    private async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        => await _client.OpenReadAsync(path, cancellationToken)
           ?? throw new FileNotFoundException($"'{path}' is not in the storage.", path);

    private async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(path, cancellationToken);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken)
            .WithStorageTimeoutAsync("reading", path, _timeProvider, cancellationToken);
        return buffer.ToArray();
    }

    private static string DecodeText(byte[] content)
    {
        // A reader, not Encoding.GetString: it drops a byte order mark, which the JSON parser rejects.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private readonly record struct StorageListing(string Path, bool IsFolder, StorageVersion Version);
}

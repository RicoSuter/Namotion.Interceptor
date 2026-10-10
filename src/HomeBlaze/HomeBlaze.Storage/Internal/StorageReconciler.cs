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

        // In the order of the paths, so that files are loaded in the same order in every pass.
        var paths = new string[listing.Count];
        listing.Keys.CopyTo(paths, 0);
        Array.Sort(paths, StringComparer.Ordinal);

        var hasMovedSubjects = followsUnfinishedPass;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listed = listing[path];

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
                    await RefreshAsync(entry, listed.Version, content: null, cancellationToken);
                }
            }
            // By the token and not by the kind of exception: subject code that gives up throws a cancellation of
            // its own. An unresponsive storage ends the pass, because every file after this one would fail as well.
            catch (Exception exception) when (exception is not StorageUnresponsiveException && !cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(exception, "Failed to load: {Path}", listed.Path);
                RecordFailure(listed, exception);
            }
        }

        Apply(hasMovedSubjects);
        _hasUnfinishedPass = false;
    }

    /// <summary>
    /// Builds the children of every folder from the index and assigns those that changed. Does nothing when no
    /// entry was added, replaced or removed since the last time it completed.
    /// </summary>
    /// <param name="keepRemovedUntilAdded">
    /// Assigns in two steps: first everything that is added, then everything that is removed. Needed when a
    /// subject moved, because an assignment detaches before it attaches and would stop and restart the subject.
    /// </param>
    public void Apply(bool keepRemovedUntilAdded = false)
    {
        if (!_index.HasUnappliedChanges)
        {
            return;
        }

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
            // An entry never gets another subject, so the key it holds or asked for is still its key.
            var key = entry.Key ?? (entry.IsFolder ? StoragePath.GetName(entry.Path) : GetChildKey(entry.Path, entry.Subject!));
            if (!childrenByFolder.TryGetValue(entry.Parent, out var siblings) || siblings.ContainsKey(key))
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

        // Only here: an assignment that throws leaves the rest of the tree to the next call.
        _index.MarkApplied();
    }

    /// <summary>
    /// Checks whether a new subject at the path could be placed: its key is free in its folder, or, when that
    /// folder is not in the tree, the name of the folder is free where it would be placed.
    /// </summary>
    public bool IsKeyFree(string path, IInterceptorSubject subject)
        => IsKeyFree(StoragePath.GetParent(path), GetChildKey(path, subject));

    public async Task<StorageVersion> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        var blobs = await _client.GetBlobsAsync([path], cancellationToken);
        var blob = blobs.FirstOrDefault();
        return blob == null ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
    }

    /// <summary>
    /// Brings the subject of a file in line with content that was just written to the file, and records the
    /// version and the hash of the file. A subject that the content replaces is placed in the tree.
    /// </summary>
    public async Task RefreshWrittenAsync(StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        // Taken before the subject reads the file: taken after, it could be of a later content than the subject has.
        var version = await GetVersionAsync(entry.Path, cancellationToken);
        if (await RefreshAsync(entry, version, content, cancellationToken))
        {
            Apply();
        }
    }

    private static string GetChildKey(string path, IInterceptorSubject subject)
        => subject is IConfigurable && IsJson(path)
            ? Path.GetFileNameWithoutExtension(path)
            : StoragePath.GetName(path);

    private static bool IsJson(string path)
        => Path.GetExtension(path).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);

    private bool IsKeyFree(string parent, string key)
    {
        // A folder that is not in the tree is placed together with what is inside it. What has to be free then
        // is the name of that folder, in the first folder above it that is in the tree.
        while (parent.Length > 0)
        {
            if (_index.TryGet(parent, out var folder))
            {
                if (!folder.IsFolder)
                {
                    return false;
                }

                if (folder.State == StorageEntryState.Placed)
                {
                    break;
                }
            }

            key = StoragePath.GetName(parent);
            parent = StoragePath.GetParent(parent);
        }

        foreach (var entry in _index.Entries)
        {
            if (entry.State == StorageEntryState.Placed && entry.Key == key && entry.Parent == parent)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, StorageListing>> ListAsync(CancellationToken cancellationToken)
    {
        var blobs = await _client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        var listing = new Dictionary<string, StorageListing>(blobs.Count, StringComparer.Ordinal);
        foreach (var blob in blobs)
        {
            var path = StoragePath.FromBlob(blob);
            if (path.Length == 0 || StoragePathFilter.IsIgnored(path))
            {
                continue;
            }

            var version = blob.IsFolder ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
            listing[path] = new StorageListing(path, blob.IsFolder, version);
        }

        // Not every backend lists the folders of its files.
        HashSet<string>? missingFolders = null;
        var listedPaths = listing.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var path in listing.Keys)
        {
            for (var parent = StoragePath.GetParent(path.AsSpan());
                 parent.Length > 0 && !listedPaths.ContainsKey(parent);
                 parent = StoragePath.GetParent(parent))
            {
                missingFolders ??= new HashSet<string>(StringComparer.Ordinal);
                missingFolders.GetAlternateLookup<ReadOnlySpan<char>>().Add(parent);
            }
        }

        if (missingFolders != null)
        {
            foreach (var folder in missingFolders)
            {
                listing[folder] = new StorageListing(folder, true, default);
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

        List<StorageEntry>? missingEntries = null;
        foreach (var entry in _index.Entries)
        {
            if (!listing.TryGetValue(entry.Path, out var listed) || listed.IsFolder != entry.IsFolder)
            {
                (missingEntries ??= []).Add(entry);
            }
        }

        if (missingEntries == null)
        {
            return movableEntries;
        }

        foreach (var entry in missingEntries)
        {
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
               IsKeyFree(entry.Parent, entry.Key);
    }

    /// <returns>True when an existing subject was moved to the path instead of creating one.</returns>
    private async Task<bool> AddAsync(
        StorageListing listed,
        Dictionary<string, List<StorageEntry>> movableEntries,
        CancellationToken cancellationToken)
    {
        var entry = new StorageEntry { Path = listed.Path, IsFolder = false, Version = listed.Version };
        var isMoved = false;

        if (IsJson(listed.Path))
        {
            var content = await ReadAsync(listed.Path, cancellationToken);
            entry.Hash = StorageHash.Compute(content);

            // More than one candidate cannot be told apart, so none of them is moved.
            // Only into another folder: the registry keeps the old key of a subject that is re-keyed within one dictionary.
            if (movableEntries.Remove(entry.Hash, out var candidates) && candidates.Count == 1 &&
                candidates[0].Parent != entry.Parent)
            {
                entry.Subject = candidates[0].Subject;
                isMoved = true;
            }
            else
            {
                entry.Subject = CreateFromJson(listed.Path, DecodeText(content), listed.Version);
            }
        }
        else
        {
            IInterceptorSubject subject;
            using (ExecutionContext.SuppressFlow())
            {
                subject = _subjectFactory.CreateFile(_storage, listed.Path);
            }

            if (subject is GenericFile genericFile)
            {
                SetMetadata(genericFile, listed.Version);
            }
            else
            {
                // Hashed before the subject reads the file, so the hash is never of a later content than the subject has.
                entry.Hash = await ComputeHashAsync(listed.Path, cancellationToken);
                if (subject is IStorageFile file)
                {
                    SetMetadata(file, listed.Version);
                    await file.OnFileChangedAsync(cancellationToken);
                }
            }

            entry.Subject = subject;
        }

        _index.Set(entry);
        return isMoved;
    }

    private IInterceptorSubject CreateFromJson(string path, string json, StorageVersion version)
    {
        IInterceptorSubject subject;

        // A configurable subject enters the context when it is created, which runs lifecycle handlers as an
        // assignment in Apply does.
        using (ExecutionContext.SuppressFlow())
        {
            subject = _subjectFactory.CreateFromJson(_storage, path, json);
        }

        if (subject is JsonFile file)
        {
            SetMetadata(file, version);
        }

        return subject;
    }

    /// <summary>
    /// Brings the subject of an entry in line with its file, and records the version and the hash of the file.
    /// </summary>
    /// <param name="entry">An entry with a subject.</param>
    /// <param name="version">The version of the file as the storage reports it.</param>
    /// <param name="content">The content of the file when the caller has it. Otherwise it is read from the storage.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>
    /// True when the content made the file something else, so that a new entry with another subject replaced
    /// the given one. <see cref="Apply"/> then exchanges the subjects in the tree.
    /// </returns>
    private async Task<bool> RefreshAsync(
        StorageEntry entry, StorageVersion version, byte[]? content, CancellationToken cancellationToken)
    {
        if (entry.Subject is GenericFile genericFile)
        {
            // Holds nothing but size and time, so there is no content to compare.
            SetMetadata(genericFile, version);
            entry.Version = version;
            return false;
        }

        string hash;
        if (entry.Subject is IStorageFile file and not JsonFile)
        {
            // The subject reads the file itself, so its bytes are only hashed here.
            hash = content != null ? StorageHash.Compute(content) : await ComputeHashAsync(entry.Path, cancellationToken);
            SetMetadata(file, version);
            if (hash != entry.Hash)
            {
                await file.OnFileChangedAsync(cancellationToken);
                _logger?.LogInformation("Reloaded: {Path}", entry.Path);
            }
        }
        else
        {
            content ??= await ReadAsync(entry.Path, cancellationToken);
            hash = StorageHash.Compute(content);
            if (hash != entry.Hash && await ApplyContentAsync(entry, version, hash, content, cancellationToken))
            {
                return true;
            }

            if (entry.Subject is JsonFile jsonFile)
            {
                SetMetadata(jsonFile, version);
            }
        }

        entry.Hash = hash;
        entry.Version = version;
        return false;
    }

    /// <summary>
    /// Applies changed content to the subject of a file whose subject is made from its text.
    /// </summary>
    /// <returns>True when a new entry with another subject replaced the given one.</returns>
    private async Task<bool> ApplyContentAsync(
        StorageEntry entry, StorageVersion version, string hash, byte[] content, CancellationToken cancellationToken)
    {
        var json = DecodeText(content);

        // Text that is not valid JSON fails here for a configurable subject, which then keeps what it has:
        // a file that is being written is no reason to stop a device.
        if (entry.Subject is IConfigurable configurable && (!IsJson(entry.Path) || FileSubjectFactory.DescribesTypeOf(entry.Subject, json)))
        {
            // Can attach nested subjects, see the creation of a subject from JSON.
            using (ExecutionContext.SuppressFlow())
            {
                _serializer.UpdateConfiguration(entry.Subject, json);
            }

            await configurable.ApplyConfigurationAsync(cancellationToken);
            _logger?.LogInformation("Reloaded: {Path}", entry.Path);
            return false;
        }

        if (!IsJson(entry.Path))
        {
            return false;
        }

        // The file is something else now: a plain file that became a subject, a subject of another type, or a
        // subject that became a plain file. Only a plain file that stays one keeps its instance.
        var subject = CreateFromJson(entry.Path, json, version);
        if (subject is JsonFile && entry.Subject is JsonFile)
        {
            return false;
        }

        _index.Set(new StorageEntry { Path = entry.Path, IsFolder = false, Version = version, Hash = hash, Subject = subject });
        _logger?.LogInformation("Replaced the subject of: {Path}", entry.Path);
        return true;
    }

    private static void SetMetadata(IStorageFile file, StorageVersion version)
    {
        file.FileSize = version.Size;
        file.LastModified = version.Modified?.UtcDateTime ?? file.LastModified;
    }

    private void RecordFailure(StorageListing listed, Exception exception)
    {
        // Recording the version makes the next pass wait for another change instead of failing again on every
        // pass. Not after an IO error: that is often over by the next pass, such as a file its writer still holds.
        var version = exception is IOException ? default : listed.Version;

        // A subject that is in the tree stays there with what it has.
        if (_index.TryGet(listed.Path, out var entry) && entry.Subject != null)
        {
            entry.Version = version;
            return;
        }

        _index.Set(new StorageEntry
        {
            Path = listed.Path,
            IsFolder = false,
            Version = version,
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

    /// <summary>
    /// Hashes the file without holding its content in memory.
    /// </summary>
    private async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(path, cancellationToken);
        return await StorageHash.ComputeAsync(stream, cancellationToken)
            .WithStorageTimeoutAsync("hashing", path, _timeProvider, cancellationToken);
    }

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

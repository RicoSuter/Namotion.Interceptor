using System.Text;
using System.Text.Json;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Brings the subject tree of a storage in line with the storage itself. A pass lists the storage once,
/// compares the listing with the index of what was applied last, loads what is new or changed, and assigns
/// the children of each changed folder once.
/// </summary>
/// <remarks>Not thread-safe. Every member runs on the <see cref="StorageWorker"/>.</remarks>
internal sealed class StorageReconciler
{
    internal const int MaximumUnfinishedNamedPathCount = 1000;

    private readonly IBlobStorage _client;
    private readonly FluentStorageContainer _storage;
    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly StorageIndex _index;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    // Cancelled when the connection ends. Nothing is assigned to the tree after that.
    private readonly CancellationToken _connectionToken;

    // What the pass that runs was asked to look at. Kept after a pass that ended before it applied its result,
    // for the pass after it. More paths than the maximum are kept as "every file".
    private IReadOnlySet<string>? _unfinishedNamedPaths;
    private bool _isEveryFileNamedByUnfinishedPass;

    // True from the move of a subject to another entry until the tree was assigned from the index.
    private bool _hasUnappliedMove;

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
    /// <exception cref="StorageRootUnavailableException">
    /// The root of the storage could not be examined. The pass ends in the same way.
    /// </exception>
    /// <remarks>
    /// A pass that ends early leaves what it was asked to look at to the next pass, which looks at that as well.
    /// More than <see cref="MaximumUnfinishedNamedPathCount"/> paths are left as every file.
    /// </remarks>
    public async Task ReconcileAsync(IReadOnlySet<string> namedPaths, bool allNamed, CancellationToken cancellationToken)
    {
        var isEveryFileNamed = allNamed || _isEveryFileNamedByUnfinishedPass;
        if (!isEveryFileNamed && _unfinishedNamedPaths is { Count: > 0 } unfinishedNamedPaths)
        {
            namedPaths = namedPaths.Count == 0
                ? unfinishedNamedPaths
                : new HashSet<string>(unfinishedNamedPaths.Concat(namedPaths), StringComparer.Ordinal);
        }

        // Bounded: while no pass finishes, every event adds its paths, also those of files that are long gone.
        var isKeptAsEveryFile = isEveryFileNamed || namedPaths.Count > MaximumUnfinishedNamedPathCount;
        _unfinishedNamedPaths = isKeptAsEveryFile ? null : namedPaths;
        _isEveryFileNamedByUnfinishedPass = isKeptAsEveryFile;

        var listing = await ListAsync(cancellationToken);
        var movableEntries = RemoveMissingEntries(listing);

        // In the order of the paths, so that files are loaded in the same order in every pass.
        var paths = new string[listing.Count];
        listing.Keys.CopyTo(paths, 0);
        Array.Sort(paths, StringComparer.Ordinal);

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
                var isChanged = !_index.TryGet(listed.Path, out var entry) || entry.Version != listed.Version ||
                                isEveryFileNamed || namedPaths.Contains(listed.Path);
                if (StorageMetadataPath.IsMetadataFile(listed.Path))
                {
                    if (isChanged)
                    {
                        await LoadMetadataAsync(listed, entry, cancellationToken);
                    }
                }
                else if (entry?.Subject != null)
                {
                    if (isChanged)
                    {
                        await RefreshAsync(entry, listed.Version, content: null, cancellationToken);
                    }
                }
                else if (isChanged || (entry is { IsKeyTaken: true } && IsKeyFree(entry.Parent, entry.Key!)))
                {
                    await AddAsync(listed, movableEntries, cancellationToken);
                }
            }
            // By the token and not by the kind of exception: subject code that gives up throws a cancellation of
            // its own. A storage that is unresponsive or whose root cannot be examined ends the pass, because
            // every file after this one would fail as well. Another storage in that state, such as one that is
            // a file of this one, fails only its file.
            catch (Exception exception) when (!IsFailureOfOwnStorage(exception) && !cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(exception, "Failed to load: {Path}", listed.Path);
                RecordFailure(listed, exception);
            }
        }

        Apply();

        _unfinishedNamedPaths = null;
        _isEveryFileNamedByUnfinishedPass = false;
    }

    private bool IsFailureOfOwnStorage(Exception exception)
        => exception switch
        {
            StorageUnresponsiveException unresponsive => ReferenceEquals(unresponsive.Storage, _client),
            StorageRootUnavailableException unavailable => ReferenceEquals(unavailable.Storage, _client),
            _ => false
        };

    /// <summary>
    /// Builds the children of every folder from the index and assigns those that changed, then passes the content
    /// of the metadata files on to what they describe. Does nothing when no entry was added, replaced or removed
    /// since the last time it completed.
    /// </summary>
    public void Apply()
    {
        if (!_index.HasUnappliedChanges)
        {
            return;
        }

        var childrenByFolder = new Dictionary<string, Dictionary<string, IInterceptorSubject>>(StringComparer.Ordinal)
        {
            [string.Empty] = new()
        };

        // Placed entries go first, so one that is in the tree never loses its key to a newcomer.
        // Within each group a folder comes before what is inside it.
        var candidates = _index.Entries
            .Where(entry => entry.IsFolder || entry.Subject != null)
            .OrderBy(entry => !entry.IsPlaced)
            .ThenBy(entry => StoragePath.GetDepth(entry.Path))
            .ThenBy(entry => !entry.IsFolder)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        List<StorageEntry>? newlyPlacedEntries = null;
        foreach (var entry in candidates)
        {
            // An entry never gets another subject, so the key it holds or asked for is still its key.
            var key = entry.Key ?? (entry.IsFolder ? StoragePath.GetName(entry.Path) : GetChildKey(entry.Path, entry.Subject!));
            if (!childrenByFolder.TryGetValue(entry.Parent, out var siblings) || siblings.ContainsKey(key))
            {
                if (!entry.IsKeyTaken)
                {
                    _logger?.LogWarning("Skipping '{Path}': key \"{Key}\" is taken or its folder is not placed", entry.Path, key);
                }

                _subjectFactory.Release(entry.Subject);

                entry.Key = key;
                entry.Subject = null;
                continue;
            }

            if (!entry.IsPlaced)
            {
                (newlyPlacedEntries ??= []).Add(entry);
            }

            if (entry.IsFolder)
            {
                entry.Subject ??= new VirtualFolder(_storage, entry.Path + "/");
                childrenByFolder[entry.Path] = new Dictionary<string, IInterceptorSubject>();
            }

            siblings.Add(key, entry.Subject!);
            entry.Key = key;
        }

        // Deepest first, the root last: a new folder is attached with its children already in place.
        var folders = childrenByFolder.Keys
            .OrderByDescending(path => path.Length == 0 ? -1 : StoragePath.GetDepth(path))
            .ToList();

        // The connection that follows an ended one owns the tree.
        _connectionToken.ThrowIfCancellationRequested();

        // Without the flow of this item: work that a lifecycle handler starts here would otherwise count as
        // part of the item, and its calls into the storage would be refused for as long as the item runs.
        using (ExecutionContext.SuppressFlow())
        {
            // When a subject moved, everything is added before anything is removed: one assignment detaches
            // before it attaches, which would stop and restart the subject.
            if (_hasUnappliedMove)
            {
                var placedSubjects = new HashSet<IInterceptorSubject>(
                    childrenByFolder.Values.SelectMany(children => children.Values), ReferenceEqualityComparer.Instance);

                // Added in two steps. In the first a subject that moves away keeps its key: its new holder
                // would detach it there, before the folder it moves to is assigned. The second gives the key
                // to that holder, so a folder that takes over a key is attached with what moved into it
                // before that is removed from where it was.
                foreach (var isKeyKeptByMovedSubject in (ReadOnlySpan<bool>)[true, false])
                {
                    foreach (var folder in folders)
                    {
                        var union = new Dictionary<string, IInterceptorSubject>(GetChildren(folder));
                        foreach (var (key, subject) in childrenByFolder[folder])
                        {
                            if (!isKeyKeptByMovedSubject || !union.TryGetValue(key, out var current) || !placedSubjects.Contains(current))
                            {
                                union[key] = subject;
                            }
                        }

                        AssignChildren(folder, union);
                    }
                }
            }

            foreach (var folder in folders)
            {
                AssignChildren(folder, childrenByFolder[folder]);
            }

            // A storage that has left the context, such as one whose file was deleted while this pass ran, no
            // longer attaches what is assigned to it. Nothing would ever detach those subjects.
            if (newlyPlacedEntries != null)
            {
                foreach (var entry in newlyPlacedEntries)
                {
                    _subjectFactory.Release(entry.Subject);
                }
            }

            // After the assignment, so that every subject it placed is registered and can take its metadata.
            ApplyMetadata();
        }

        // Only here: an assignment that throws leaves the rest of the tree to the next call.
        _index.MarkApplied();
        _hasUnappliedMove = false;
    }

    /// <summary>
    /// Gives every placed folder the metadata of its <c>.metadata.json</c> and the root the order in its own, and
    /// every placed subject of a JSON file the metadata of the file beside it, or none.
    /// </summary>
    private void ApplyMetadata()
    {
        var folderMetadata = new Dictionary<string, StorageMetadataDocument>(StringComparer.Ordinal);
        var subjectMetadata = new Dictionary<(string Folder, string Name), StorageMetadataDocument>();
        foreach (var entry in _index.Entries)
        {
            if (entry is not { IsMetadata: true, Metadata: { } metadata })
            {
                continue;
            }

            var name = StorageMetadataPath.GetDescribedName(entry.Path);
            if (name.Length == 0)
            {
                folderMetadata[entry.Parent] = metadata;
            }
            else
            {
                subjectMetadata[(entry.Parent, name)] = metadata;
            }
        }

        _storage.ApplyMetadata(folderMetadata.GetValueOrDefault(string.Empty));
        foreach (var entry in _index.Entries)
        {
            if (!entry.IsPlaced)
            {
                continue;
            }

            if (entry.Subject is VirtualFolder folder)
            {
                folder.ApplyMetadata(folderMetadata.GetValueOrDefault(entry.Path));
            }
            else if (entry.Subject is IConfigurable && StoragePath.IsJson(entry.Path))
            {
                entry.Subject.SetSubjectMetadata(subjectMetadata.GetValueOrDefault((entry.Parent, entry.Key!))?.ToSubjectMetadata());
            }
        }
    }

    /// <summary>
    /// Checks whether a new subject at the path could be placed: its key is free in its folder, or, when that
    /// folder is not in the tree, the name of the folder is free where it would be placed.
    /// </summary>
    public bool IsKeyFree(string path, IInterceptorSubject subject)
        => IsKeyFree(StoragePath.GetParent(path), GetChildKey(path, subject));

    /// <summary>
    /// Releases every subject of the index that never reached the tree, for when the connection has ended or
    /// the storage was stopped, and no pass will place them. Their entries are left as those of files that
    /// were not loaded.
    /// </summary>
    public void ReleaseUnplacedSubjects()
    {
        foreach (var entry in _index.Entries)
        {
            _subjectFactory.Release(entry.Subject);

            // Kept, an operation after a stop would place the subject and attach again what was released
            // here. Not for a subject that moved: the tree still holds it, and it was not released.
            if (entry is { IsPlaced: false, Subject: not null } && entry.Subject.GetReferenceCount() == 0)
            {
                entry.Subject = null;
                entry.Version = default;
                entry.Hash = null;
            }
        }
    }

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
    /// <returns>True when the content replaced the subject.</returns>
    public async Task<bool> RefreshWrittenAsync(StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        // Taken before the subject reads the file: taken after, it could be of a later content than the subject has.
        var version = await GetVersionAsync(entry.Path, cancellationToken);
        if (!await RefreshAsync(entry, version, content, cancellationToken))
        {
            return false;
        }

        Apply();
        return true;
    }

    private static string GetChildKey(string path, IInterceptorSubject subject)
        => subject is IConfigurable && StoragePath.IsJson(path)
            ? Path.GetFileNameWithoutExtension(path)
            : StoragePath.GetName(path);

    private bool IsKeyFree(string parent, string key)
    {
        while (parent.Length > 0)
        {
            if (_index.TryGet(parent, out var folder))
            {
                if (!folder.IsFolder)
                {
                    return false;
                }

                if (folder.IsPlaced)
                {
                    break;
                }
            }

            key = StoragePath.GetName(parent);
            parent = StoragePath.GetParent(parent);
        }

        foreach (var entry in _index.Entries)
        {
            if (entry.IsPlaced && entry.Key == key && entry.Parent == parent)
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

            // The path filter only inspects segments and cannot tell a folder from a file, so a directory named
            // like the folder metadata file would otherwise pass through and shadow the file of its parent.
            if (blob.IsFolder && string.Equals(StoragePath.GetName(path), StorageMetadataPath.FolderFileName, StringComparison.Ordinal))
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
    /// Removes the entries that are gone or changed kind, and returns those of them whose configurable subject
    /// is in the tree by content hash: a new file with the same content can be the same subject under a new
    /// path. A subject of the others that never reached the tree is released.
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

            if (entry is { IsFolder: false, Subject: IConfigurable, Hash: not null } && entry.Subject.GetReferenceCount() > 0)
            {
                if (!movableEntries.TryGetValue(entry.Hash, out var entries))
                {
                    movableEntries[entry.Hash] = entries = [];
                }

                entries.Add(entry);
            }
            else
            {
                _subjectFactory.Release(entry.Subject);
            }
        }

        return movableEntries;
    }

    private async Task AddAsync(
        StorageListing listed,
        Dictionary<string, List<StorageEntry>> movableEntries,
        CancellationToken cancellationToken)
    {
        var entry = new StorageEntry { Path = listed.Path, IsFolder = false, Version = listed.Version };

        if (StoragePath.IsJson(listed.Path))
        {
            var content = await ReadAsync(listed.Path, cancellationToken);
            entry.Hash = StorageHash.Compute(content);

            // More than one candidate cannot be told apart, so none of them is moved.
            // Only into another folder: the registry keeps the old key of a subject that is re-keyed within one dictionary.
            if (movableEntries.Remove(entry.Hash, out var candidates) && candidates.Count == 1 &&
                candidates[0].Parent != entry.Parent)
            {
                entry.Subject = candidates[0].Subject;
                _hasUnappliedMove = true;
            }
            else
            {
                entry.Subject = CreateFromJson(listed.Path, DecodeText(content), listed.Version);
            }
        }
        else
        {
            var subject = _subjectFactory.CreateFile(_storage, StoragePath.ToFullPath(listed.Path));
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
    }

    /// <summary>
    /// Reads a metadata file into its entry. A file that is not a metadata file is logged and recorded with no
    /// metadata, so that the next pass waits for another change of it.
    /// </summary>
    private async Task LoadMetadataAsync(StorageListing listed, StorageEntry? entry, CancellationToken cancellationToken)
    {
        var content = await ReadAsync(listed.Path, cancellationToken);
        var hash = StorageHash.Compute(content);
        if (entry is { IsMetadata: true } && entry.Hash == hash)
        {
            entry.Version = listed.Version;
            return;
        }

        StorageMetadataDocument? metadata;
        try
        {
            metadata = StorageMetadataDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(exception, "Ignoring a metadata file that is not valid: {Path}", listed.Path);
            metadata = null;
        }

        // Replaced rather than changed: the index then marks the tree as unapplied, and Apply passes the metadata on.
        _index.Set(new StorageEntry
        {
            Path = listed.Path,
            IsFolder = false,
            IsMetadata = true,
            Version = listed.Version,
            Hash = hash,
            Metadata = metadata
        });
    }

    private IInterceptorSubject CreateFromJson(string path, string json, StorageVersion version)
    {
        var subject = _subjectFactory.CreateFromJson(_storage, StoragePath.ToFullPath(path), json);
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
                // A load that fails half way has changed the subject, which then holds neither content.
                entry.Hash = null;

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
        if (entry.Subject is IConfigurable configurable && (!StoragePath.IsJson(entry.Path) || FileSubjectFactory.DescribesTypeOf(entry.Subject, json)))
        {
            // From here on the subject holds neither content until the configuration is applied. Not earlier:
            // text that was rejected above has not touched the subject.
            entry.Hash = null;

            // Can attach nested subjects, see the creation of a subject from JSON.
            using (ExecutionContext.SuppressFlow())
            {
                _serializer.UpdateConfiguration(entry.Subject, json);
            }

            await configurable.ApplyConfigurationAsync(cancellationToken);
            _logger?.LogInformation("Reloaded: {Path}", entry.Path);
            return false;
        }

        if (!StoragePath.IsJson(entry.Path))
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

        _subjectFactory.Release(entry.Subject);

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

        // A subject that is in the tree stays there with what it has, and so does metadata that was read before.
        if (_index.TryGet(listed.Path, out var entry) && (entry.Subject != null || entry.IsMetadata))
        {
            entry.Version = version;
            return;
        }

        _index.Set(new StorageEntry { Path = listed.Path, IsFolder = false, Version = version });
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

        if (folder.Length == 0)
        {
            _storage.Children = children;
        }
        else
        {
            ((VirtualFolder)GetEntry(folder).Subject!).Children = children;
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
        var hashing = StorageHash.ComputeAsync(stream, cancellationToken);
        await hashing.WithStorageTimeoutAsync("hashing", path, _timeProvider, cancellationToken);
        return await hashing;
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

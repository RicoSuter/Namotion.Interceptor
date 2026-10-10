using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage root using FluentStorage. Implements IStorageContainer and IConfigurationWriter.
/// Supports FileSystemWatcher for reactive file monitoring on disk storage.
/// </summary>
[InterceptorSubject]
public partial class FluentStorageContainer :
    BackgroundService,
    IStorageContainer, IConfigurationWriter, ITitleProvider, IIconProvider, IConfigurable
{
    private IBlobStorage? _client;

    private IBlobStorage Client => _client
        ?? throw new InvalidOperationException("Storage not connected");

    private readonly StoragePathRegistry _pathRegistry = new();

    // Guards every update of the hierarchy (Children here and on the folders below) together with the path
    // registry, except for ScanAsync. Children is copied, modified and reassigned, so unsynchronized callers
    // overwrite each other's update. The helpers that take it are also called while it is held, which relies
    // on Lock being reentrant. It is never held across an await: a subject loads outside of it, so what was
    // read from disk or the registry before has to be checked again inside.
    private readonly Lock _hierarchyLock = new();
    private readonly FileSubjectFactory _subjectFactory;
    private readonly StorageHierarchyManager _hierarchyManager;
    private readonly ILogger<FluentStorageContainer>? _logger;

    private readonly ConfigurableSubjectSerializer _serializer;

    private StorageFileWatcher? _fileWatcher;
    private string? _storageDirectory;
    private JsonSubjectSynchronizer? _jsonSyncHelper;

    /// <summary>
    /// Storage type identifier (e.g., "disk", "azure-blob").
    /// </summary>
    [Configuration]
    public partial string StorageType { get; set; }

    /// <summary>
    /// Connection string or path for the storage.
    /// </summary>
    [Configuration]
    public partial string ConnectionString { get; set; }

    /// <summary>
    /// Container name (for cloud storage).
    /// </summary>
    [Configuration]
    public partial string? ContainerName { get; set; }

    /// <summary>
    /// Whether file system watching is enabled. Default is true.
    /// </summary>
    [Configuration]
    public partial bool EnableFileWatching { get; set; }

    /// <summary>
    /// Child subjects (files and folders).
    /// </summary>
    [InlinePaths]
    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    /// <summary>
    /// Current status of the storage.
    /// </summary>
    [State]
    public partial StorageStatus Status { get; set; }

    public string Title => string.IsNullOrEmpty(ConnectionString)
        ? "Storage"
        : Path.GetFileName(ConnectionString.TrimEnd('/', '\\'));

    public string IconName => "Storage";

    [Derived]
    public string IconColor => Status switch
    {
        StorageStatus.Connected => "Success",
        StorageStatus.Error => "Error",
        _ => "Warning"
    };

    public FluentStorageContainer(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        IServiceProvider serviceProvider,
        ILogger<FluentStorageContainer>? logger = null)
    {
        _subjectFactory = new FileSubjectFactory(typeRegistry, serializer, serviceProvider, logger);
        _hierarchyManager = new StorageHierarchyManager(logger);
        _serializer = serializer;
        _logger = logger;

        StorageType = "disk";
        ConnectionString = string.Empty;
        EnableFileWatching = true;
        Children = new Dictionary<string, IInterceptorSubject>();
        Status = StorageStatus.Disconnected;
    }
        
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConnectAsync(stoppingToken);
    }

    /// <summary>
    /// IConfigurable implementation - called after configuration properties are updated.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Reconnect if configuration changed
        return ConnectAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the file system path of a storage-relative path, resolving a relative
    /// <see cref="ConnectionString"/> against the instance data directory.
    /// </summary>
    internal string GetFileSystemPath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(_storageDirectory ?? ResolveStorageDirectory(), relativePath.TrimStart('/', '\\')));
    }

    private string ResolveStorageDirectory()
    {
        var baseDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory
            ?? Directory.GetCurrentDirectory();

        return string.IsNullOrEmpty(ConnectionString)
            ? baseDirectory
            : Path.GetFullPath(ConnectionString, baseDirectory);
    }

    /// <summary>
    /// Initializes the storage client based on configuration.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var isInMemory = StorageType == "inmemory";
        if (!isInMemory && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        Status = StorageStatus.Initializing;
        try
        {
            _storageDirectory = isInMemory ? null : ResolveStorageDirectory();
            _client = StorageType switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
                "inmemory" => StorageFactory.Blobs.InMemory(),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };

            _jsonSyncHelper = new JsonSubjectSynchronizer(_pathRegistry, _serializer, _client, _logger);

            Status = StorageStatus.Connected;
            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                isInMemory ? "(in-memory)" : _storageDirectory);

            await ScanAsync(cancellationToken);

            if (EnableFileWatching && !isInMemory)
            {
                StartFileWatching();
            }
        }
        catch (Exception ex)
        {
            Status = StorageStatus.Error;
            _logger?.LogError(ex, "Failed to connect to storage");
            throw;
        }
    }

    /// <summary>
    /// Scans the storage and builds the subject hierarchy.
    /// </summary>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Scanning storage...");

        var blobs = await Client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        _pathRegistry.Clear();

        var children = new Dictionary<string, IInterceptorSubject>();
        foreach (var blob in blobs)
        {
            if (IsIgnored(blob))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (blob.IsFolder)
            {
                _hierarchyManager.PlaceInHierarchy(blob.FullPath, null, children, this);
                continue;
            }

            try
            {
                var subject = await _subjectFactory.CreateFromBlobAsync(Client, this, blob, cancellationToken);
                if (subject != null && _hierarchyManager.PlaceInHierarchy(blob.FullPath, subject, children, this))
                {
                    _pathRegistry.Register(subject, blob.FullPath);

                    var hash = await TryComputeJsonHashAsync(blob.FullPath, cancellationToken);
                    if (hash != null)
                    {
                        _pathRegistry.UpdateHash(blob.FullPath, hash);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create subject for blob: {Path}", blob.FullPath);
            }
        }

        Children = children;
        _logger?.LogInformation("Scan complete: Found {Count} subjects.", _pathRegistry.Count);
    }

    private void StartFileWatching()
    {
        _fileWatcher = new StorageFileWatcher(
            _storageDirectory!,
            ProcessFileEventAsync,
            ResyncAsync,
            _logger);

        _fileWatcher.Start();
    }

    /// <summary>
    /// Brings the whole hierarchy in line with what is on disk after file events were lost. Unlike
    /// <see cref="ScanAsync"/> it keeps the subjects of unchanged paths and can run while events are processed.
    /// </summary>
    internal async Task ResyncAsync()
    {
        RemoveMissingChildren(Children);
        await SyncBlobsAsync(await Client.ListAsync(recurse: true));

        _logger?.LogInformation("Synchronized storage after lost file events.");
    }

    // Temporary files are skipped because the watcher drops their events, so one that was added could never be removed.
    private static bool IsIgnored(Blob blob)
        => StoragePathFilter.IsHidden(blob.FullPath) ||
           (!blob.IsFolder && StoragePathFilter.IsTemporaryFile(blob.FullPath));

    internal async Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        // Deliberately no switch on the change type: coalesced and delayed events can describe a state
        // that no longer exists, so each path is looked up on disk instead.
        var relativePath = GetRelativePath(e.FullPath);
        if (e is RenamedEventArgs renamed)
        {
            var oldRelativePath = GetRelativePath(renamed.OldFullPath);
            if (!oldRelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                await SyncPathAsync(oldRelativePath, reportedAsCreated: false);
            }
            else if (oldRelativePath != relativePath)
            {
                // Only the casing changed. On a case-insensitive file system the old path still resolves,
                // so it is removed without looking at the disk and added again under the new casing below.
                Remove(oldRelativePath);
            }
        }

        var reportedAsCreated = e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed;
        await SyncPathAsync(relativePath, reportedAsCreated);
    }

    private string GetRelativePath(string fileSystemPath)
        => Path.GetRelativePath(_storageDirectory!, fileSystemPath).Replace('\\', '/');

    /// <summary>
    /// Brings the hierarchy in line with what is on disk at the path.
    /// </summary>
    /// <param name="relativePath">The path within the storage.</param>
    /// <param name="reportedAsCreated">
    /// Whether the event reported the path as created or renamed to. A directory is only synchronized then.
    /// </param>
    private async Task SyncPathAsync(string relativePath, bool reportedAsCreated)
    {
        if (StoragePathFilter.IsHidden(relativePath))
            return;

        var fileSystemPath = GetFileSystemPath(relativePath);
        if (Directory.Exists(fileSystemPath))
        {
            // An existing directory reports a change for every entry written inside it, and those raise their own events.
            if (reportedAsCreated)
            {
                await SyncDirectoryAsync(relativePath);
            }
        }
        else if (!File.Exists(fileSystemPath))
        {
            RemoveIfMissing(relativePath);
        }
        else if (_pathRegistry.TryGetSubject(relativePath, out var existingSubject))
        {
            await NotifyFileChangedAsync(existingSubject, relativePath, fileSystemPath);
        }
        else
        {
            await AddFileAsync(new Blob(relativePath));
        }
    }

    private async Task SyncDirectoryAsync(string relativePath)
    {
        var folder = EnsureFolder(relativePath);
        if (folder == null)
            return;

        // The directory can have replaced another one of the same name, whose entries are then gone from disk.
        RemoveMissingChildren(folder.Children);

        // A directory that is moved or copied in raises no events for the entries it already contains.
        await SyncBlobsAsync(await Client.ListAsync(folderPath: relativePath, recurse: true));

        _logger?.LogInformation("Synchronized folder from external: {Path}", relativePath);
    }

    private async Task SyncBlobsAsync(IReadOnlyCollection<Blob> blobs)
    {
        foreach (var blob in blobs)
        {
            if (IsIgnored(blob))
                continue;

            try
            {
                if (blob.IsFolder)
                {
                    EnsureFolder(blob.FullPath);
                }
                else if (_pathRegistry.TryGetSubject(blob.FullPath, out var existingSubject))
                {
                    await NotifyFileChangedAsync(existingSubject, blob.FullPath, GetFileSystemPath(blob.FullPath));
                }
                else
                {
                    await AddFileAsync(blob);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to synchronize blob: {Path}", blob.FullPath);
            }
        }
    }

    /// <summary>
    /// Returns the folder at the path, creating it when the directory exists on disk.
    /// </summary>
    private VirtualFolder? EnsureFolder(string relativePath)
    {
        lock (_hierarchyLock)
        {
            if (!Directory.Exists(GetFileSystemPath(relativePath)))
                return null;

            var folder = StorageHierarchyManager.FindFolder(relativePath, Children);
            if (folder != null)
                return folder;

            // A file of the same name was replaced by the directory.
            if (_pathRegistry.TryGetSubject(relativePath, out var replacedFile))
            {
                RemoveFromHierarchy(relativePath, replacedFile);
            }

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            _hierarchyManager.PlaceInHierarchy(relativePath, null, children, this);
            Children = children;

            return StorageHierarchyManager.FindFolder(relativePath, children);
        }
    }

    private void RemoveMissingChildren(Dictionary<string, IInterceptorSubject> children)
    {
        foreach (var child in children.Values)
        {
            if (child is VirtualFolder childFolder)
            {
                if (!RemoveIfMissing(childFolder.RelativePath))
                {
                    RemoveMissingChildren(childFolder.Children);
                }
            }
            else if (_pathRegistry.TryGetPath(child, out var path))
            {
                RemoveIfMissing(path);
            }
        }
    }

    private async Task AddFileAsync(Blob blob)
    {
        var relativePath = blob.FullPath;
        var fileSystemPath = GetFileSystemPath(relativePath);

        var subject = await _subjectFactory.CreateFromBlobAsync(Client, this, blob, CancellationToken.None);
        if (subject == null)
            return;

        var hash = await TryComputeJsonHashAsync(relativePath, CancellationToken.None);

        IInterceptorSubject? addedMeanwhile;
        lock (_hierarchyLock)
        {
            // If the file was deleted while the subject loaded, its delete event may already have run
            // and found nothing to remove.
            if (!File.Exists(fileSystemPath))
                return;

            if (!_pathRegistry.TryGetSubject(relativePath, out addedMeanwhile))
            {
                // A directory of the same name was replaced by the file.
                RemoveFolder(relativePath);

                if (!AddToHierarchy(relativePath, subject))
                    return;

                if (hash != null)
                {
                    _pathRegistry.UpdateHash(relativePath, hash);
                }
            }
        }

        if (addedMeanwhile != null)
        {
            // A concurrent event added the file first and may have read it before the content this one saw.
            await NotifyFileChangedAsync(addedMeanwhile, relativePath, fileSystemPath);
            return;
        }

        _logger?.LogInformation("Added file from external: {Path}", relativePath);
    }

    private async Task<string?> TryComputeJsonHashAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(relativePath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var content = await Client.ReadTextAsync(relativePath, cancellationToken: cancellationToken);
            return StoragePathRegistry.ComputeHash(content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to compute hash for: {Path}", relativePath);
            return null;
        }
    }

    private async Task NotifyFileChangedAsync(IInterceptorSubject existingSubject, string relativePath, string fileSystemPath)
    {
        if (existingSubject is IStorageFile storageFile)
        {
            try
            {
                await storageFile.OnFileChangedAsync(CancellationToken.None);
                _logger?.LogInformation("Notified file of change: {Path}", relativePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to notify file of change: {Path}", relativePath);
            }
        }
        else if (existingSubject is IConfigurable)
        {
            await _jsonSyncHelper!.TryRefreshAsync(existingSubject, relativePath, fileSystemPath, CancellationToken.None);
        }
    }

    /// <summary>
    /// Removes the subject or folder at the path unless the path is on disk.
    /// </summary>
    /// <returns>True when the path is gone from disk.</returns>
    private bool RemoveIfMissing(string relativePath)
    {
        var fileSystemPath = GetFileSystemPath(relativePath);
        lock (_hierarchyLock)
        {
            // A path that reappeared meanwhile is refreshed or added by its own event, removing it now would lose it.
            if (File.Exists(fileSystemPath) || Directory.Exists(fileSystemPath))
                return false;

            Remove(relativePath);
            return true;
        }
    }

    private void Remove(string relativePath)
    {
        lock (_hierarchyLock)
        {
            if (_pathRegistry.TryGetSubject(relativePath, out var subject))
            {
                RemoveFromHierarchy(relativePath, subject);
                _logger?.LogInformation("Removed deleted file: {Path}", relativePath);
            }
            else if (RemoveFolder(relativePath))
            {
                _logger?.LogInformation("Removed deleted folder: {Path}", relativePath);
            }
        }
    }

    /// <summary>
    /// IConfigurationWriter - called by ConfigurationManager background thread.
    /// </summary>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (_client == null)
            return false;

        if (!_pathRegistry.TryGetPath(subject, out var path))
            return false;

        var fullPath = GetFileSystemPath(path);
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        var json = _subjectFactory.Serialize(subject);
        _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(json));

        await Client.WriteTextAsync(path, json, cancellationToken: cancellationToken);

        _logger?.LogDebug("Saved subject to storage: {Path}", path);
        return true;
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var fullPath = GetFileSystemPath(path);
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        var json = _subjectFactory.Serialize(subject);
        _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(json));

        await Client.WriteTextAsync(path, json, cancellationToken: cancellationToken);

        if (AddToHierarchy(path, subject))
        {
            _logger?.LogInformation("Added subject to storage: {Path}", path);
        }
    }

    /// <summary>
    /// Places the subject in the hierarchy and registers its path.
    /// </summary>
    /// <returns>False when its key is already claimed. The subject is then neither placed nor registered.</returns>
    private bool AddToHierarchy(string path, IInterceptorSubject subject)
    {
        lock (_hierarchyLock)
        {
            var children = new Dictionary<string, IInterceptorSubject>(Children);
            if (!_hierarchyManager.PlaceInHierarchy(path, subject, children, this))
                return false;

            _pathRegistry.Register(subject, path);
            Children = children;
            return true;
        }
    }

    private void RemoveFromHierarchy(string path, IInterceptorSubject subject)
    {
        lock (_hierarchyLock)
        {
            // The registry finds a path whatever its casing, but the key in Children has the casing it was registered with.
            if (_pathRegistry.TryGetPath(subject, out var registeredPath))
            {
                path = registeredPath;
            }

            _pathRegistry.Unregister(path);

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            _hierarchyManager.RemoveFromHierarchy(path, subject, children);

            Children = children;
        }
    }

    /// <summary>
    /// Removes the folder at the path and unregisters every subject below it.
    /// </summary>
    /// <returns>False when the path does not hold a folder.</returns>
    private bool RemoveFolder(string path)
    {
        lock (_hierarchyLock)
        {
            // Looked up first because the removal works on a copy of Children, and this runs for every added file.
            if (StorageHierarchyManager.FindFolder(path, Children) == null)
                return false;

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            if (!_hierarchyManager.RemoveFolderFromHierarchy(path, children))
                return false;

            _pathRegistry.UnregisterDirectory(path);

            Children = children;
            return true;
        }
    }

    /// <summary>
    /// IStorageContainer - Gets metadata about a blob.
    /// </summary>
    public async Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var blobs = await Client.ListAsync(folderPath: Path.GetDirectoryName(path)?.Replace('\\', '/'),
            recurse: false, cancellationToken: cancellationToken);
        var blob = blobs.FirstOrDefault(b =>
            b.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            b.FullPath.TrimStart('/').Equals(path.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        if (blob == null)
            return null;

        return new BlobMetadata(blob.Size ?? 0, blob.LastModificationTime?.UtcDateTime);
    }

    /// <summary>
    /// IStorageContainer - Reads a blob from storage.
    /// </summary>
    public async Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken)
    {
        return await Client.OpenReadAsync(path, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Writes a blob to storage.
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var fullPath = GetFileSystemPath(path);
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        await Client.WriteAsync(path, content, append: false, cancellationToken: cancellationToken);
        _logger?.LogDebug("Wrote blob to storage: {Path}", path);

        // Notify the file subject to reload its in-memory state
        if (_pathRegistry.TryGetSubject(path, out var subject) && subject is IStorageFile file)
        {
            await file.OnFileChangedAsync(cancellationToken);
        }
    }

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage and removes from Children.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
    {
        // Get subject BEFORE deleting (needed for hierarchy removal)
        if (!_pathRegistry.TryGetSubject(path, out var subject))
        {
            _logger?.LogWarning("Cannot delete blob - subject not found in registry: {Path}", path);
            return;
        }

        var fullPath = GetFileSystemPath(path);
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        await Client.DeleteAsync(path, cancellationToken: cancellationToken);

        // Remove from hierarchy - uses reusable helper
        RemoveFromHierarchy(path, subject);

        _logger?.LogDebug("Deleted blob from storage: {Path}", path);
    }

    /// <summary>
    /// Opens the create subject wizard to add a new subject to this storage.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a subject by finding its path in the registry.
    /// </summary>
    public async Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        if (!_pathRegistry.TryGetPath(subject, out var path))
            throw new InvalidOperationException("Subject not found in storage registry");

        // Delegate to DeleteBlobAsync which handles file deletion and hierarchy removal
        await DeleteBlobAsync(path, cancellationToken);
    }


    public override void Dispose()
    {
        _fileWatcher?.Dispose();
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
        base.Dispose();
    }
}

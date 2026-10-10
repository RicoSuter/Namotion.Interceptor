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
            if (StoragePathFilter.IsHidden(blob.FullPath))
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
                if (subject != null)
                {
                    _hierarchyManager.PlaceInHierarchy(blob.FullPath, subject, children, this);
                    _pathRegistry.Register(subject, blob.FullPath);

                    if (Path.GetExtension(blob.FullPath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var content = await Client.ReadTextAsync(blob.FullPath, cancellationToken: cancellationToken);
                            _pathRegistry.UpdateHash(blob.FullPath, StoragePathRegistry.ComputeHash(content));
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to compute hash for: {Path}", blob.FullPath);
                        }
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
            () => ScanAsync(CancellationToken.None),
            _logger);

        _fileWatcher.Start();
    }

    internal async Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs renamed)
        {
            await SyncPathAsync(GetRelativePath(renamed.OldFullPath), isNew: false);
        }

        var isNew = e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed;
        await SyncPathAsync(GetRelativePath(e.FullPath), isNew);
    }

    private string GetRelativePath(string fullPath)
        => Path.GetRelativePath(_storageDirectory!, fullPath).Replace('\\', '/');

    /// <summary>
    /// Brings the hierarchy in line with what is on disk at the path. The event only says where to look:
    /// coalesced and delayed events can describe a state that no longer exists.
    /// </summary>
    private async Task SyncPathAsync(string relativePath, bool isNew)
    {
        if (StoragePathFilter.IsHidden(relativePath))
            return;

        var fullPath = GetFileSystemPath(relativePath);
        if (Directory.Exists(fullPath))
        {
            // An existing directory reports a change for every entry written inside it, and those raise their own events.
            if (isNew)
            {
                await AddDirectoryAsync(relativePath, fullPath);
            }
        }
        else if (!File.Exists(fullPath))
        {
            RemovePath(relativePath, fullPath);
        }
        else if (_pathRegistry.TryGetSubject(relativePath, out var existingSubject))
        {
            await NotifyFileChangedAsync(existingSubject, relativePath, fullPath);
        }
        else
        {
            await AddFileAsync(new Blob(relativePath), fullPath);
        }
    }

    private async Task AddDirectoryAsync(string relativePath, string fullPath)
    {
        if (!EnsureFolder(relativePath, fullPath))
            return;

        _logger?.LogInformation("Added folder from external: {Path}", relativePath);

        // A directory that is moved or copied in raises no events for the entries it already contains.
        var blobs = await Client.ListAsync(folderPath: relativePath, recurse: true);
        foreach (var blob in blobs)
        {
            if (StoragePathFilter.IsHidden(blob.FullPath) || StoragePathFilter.IsTemporaryFile(blob.FullPath))
                continue;

            try
            {
                var blobFullPath = GetFileSystemPath(blob.FullPath);
                if (blob.IsFolder)
                {
                    EnsureFolder(blob.FullPath, blobFullPath);
                }
                else if (!_pathRegistry.TryGetSubject(blob.FullPath, out _))
                {
                    await AddFileAsync(blob, blobFullPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create subject for blob: {Path}", blob.FullPath);
            }
        }
    }

    private bool EnsureFolder(string relativePath, string fullPath)
    {
        lock (_hierarchyLock)
        {
            if (!Directory.Exists(fullPath))
                return false;

            // A file of the same name was replaced by the directory.
            if (_pathRegistry.TryGetSubject(relativePath, out var replacedFile))
            {
                RemoveFromHierarchy(relativePath, replacedFile);
            }

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            _hierarchyManager.PlaceInHierarchy(relativePath, null, children, this);
            Children = children;
            return true;
        }
    }

    private async Task AddFileAsync(Blob blob, string fullPath)
    {
        var path = blob.FullPath;
        _logger?.LogDebug("File created: {Path}", path);

        var subject = await _subjectFactory.CreateFromBlobAsync(Client, this, blob, CancellationToken.None);
        if (subject == null)
            return;

        string? hash = null;
        if (Path.GetExtension(path).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var content = await Client.ReadTextAsync(path);
                hash = StoragePathRegistry.ComputeHash(content);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to compute hash for: {Path}", path);
            }
        }

        lock (_hierarchyLock)
        {
            // Both are checked under the lock because the subject loaded outside of it. A concurrent event may have
            // added the file meanwhile. If it was deleted meanwhile, adding it now would leave a phantom: its delete
            // event may already have run and found nothing to remove.
            if (_pathRegistry.TryGetSubject(path, out _) || !File.Exists(fullPath))
                return;

            // A directory of the same name was replaced by the file.
            RemoveFolder(path);

            if (hash != null)
            {
                _pathRegistry.UpdateHash(path, hash);
            }

            AddToHierarchy(path, subject);
        }

        _logger?.LogInformation("Added file from external: {Path}", path);
    }

    private async Task NotifyFileChangedAsync(IInterceptorSubject existingSubject, string relativePath, string fullPath)
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
            await _jsonSyncHelper!.TryRefreshAsync(existingSubject, relativePath, fullPath, CancellationToken.None);
        }
    }

    private void RemovePath(string relativePath, string fullPath)
    {
        lock (_hierarchyLock)
        {
            // Checked under the lock: when the path reappeared meanwhile, its own event has refreshed the subject
            // or will add it, and removing it now would lose it until the next scan.
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                return;

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

        // Update hierarchy - extracted to reusable method
        AddToHierarchy(path, subject);

        _logger?.LogInformation("Added subject to storage: {Path}", path);
    }

    /// <summary>
    /// Adds a subject to the hierarchy. Reusable helper for AddSubjectAsync and file watcher events.
    /// </summary>
    private void AddToHierarchy(string path, IInterceptorSubject subject)
    {
        // Children is copied, modified and reassigned, so concurrent callers (watcher events for different
        // paths, UI operations) would otherwise overwrite each other's update.
        lock (_hierarchyLock)
        {
            var children = new Dictionary<string, IInterceptorSubject>(Children);

            _pathRegistry.Register(subject, path);
            _hierarchyManager.PlaceInHierarchy(path, subject, children, this);

            Children = children;
        }
    }

    /// <summary>
    /// Removes a subject from the hierarchy. Reusable helper for delete operations and file watcher events.
    /// </summary>
    private void RemoveFromHierarchy(string path, IInterceptorSubject subject)
    {
        lock (_hierarchyLock)
        {
            _pathRegistry.Unregister(path);

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            _hierarchyManager.RemoveFromHierarchy(path, subject, children);

            Children = children;
        }
    }

    /// <summary>
    /// Removes the folder at the path and unregisters every subject below it.
    /// </summary>
    private bool RemoveFolder(string path)
    {
        lock (_hierarchyLock)
        {
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

using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly FileSubjectFactory _subjectFactory;
    private readonly StorageHierarchyManager _hierarchyManager;
    private readonly ILogger<FluentStorageContainer>? _logger;

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly TypeProvider? _typeProvider;

    // Serializes everything that swaps the client or rebuilds or swaps children: scans, file watcher
    // events and placeholder upgrades. Not reentrant: code running under it must not call
    // RunLockedAsync or WriteBlobAsync.
    private readonly SemaphoreSlim _hierarchyLock = new(1, 1);

    private StorageFileWatcher? _fileWatcher;
    private string? _storageDirectory;
    private JsonSubjectSynchronizer? _jsonSyncHelper;

    /// <summary>
    /// The active file watcher, if any.
    /// </summary>
    internal StorageFileWatcher? FileWatcher => _fileWatcher;

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
        _typeProvider = serviceProvider.GetService<TypeProvider>();
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
            if (_typeProvider is not null)
            {
                // Subscribing before the scan means a type added during the scan is not missed: its upgrade
                // waits for the lock and then sees the scanned placeholders. Removing first keeps a
                // reconnect from subscribing twice.
                _typeProvider.TypesChanged -= OnTypesChanged;
                _typeProvider.TypesChanged += OnTypesChanged;
            }

            await _hierarchyLock.WaitAsync(cancellationToken);
            try
            {
                _storageDirectory = isInMemory ? null : ResolveStorageDirectory();

                var previousClient = _client;
                _client = StorageType switch
                {
                    "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
                    "inmemory" => StorageFactory.Blobs.InMemory(),
                    _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
                };
                previousClient?.Dispose();

                _jsonSyncHelper = new JsonSubjectSynchronizer(_pathRegistry, _serializer, _client, _logger);

                Status = StorageStatus.Connected;
                _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                    isInMemory ? "(in-memory)" : _storageDirectory);

                await ScanAsync(cancellationToken);
            }
            finally
            {
                _hierarchyLock.Release();
            }

            if (EnableFileWatching && !isInMemory)
            {
                StartFileWatching();
            }
            else
            {
                _fileWatcher?.Dispose();
                _fileWatcher = null;
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
    /// Scans the storage and builds the subject hierarchy. Callers hold the hierarchy lock.
    /// </summary>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Scanning storage...");

        var blobs = await Client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        _pathRegistry.Clear();

        var children = new Dictionary<string, IInterceptorSubject>();
        foreach (var blob in blobs)
        {
            // Filter out hidden files/folders (e.g. .DS_Store, .idea)
            var name = Path.GetFileName(blob.FullPath.TrimEnd('/'));
            if (name.StartsWith('.') || blob.FullPath.Contains("/."))
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
        // Dispose the previous watcher immediately before replacing it, not at the start of
        // ConnectAsync, so it keeps covering changes while ScanAsync runs.
        _fileWatcher?.Dispose();

        StorageFileWatcher? watcher = null;
        watcher = new StorageFileWatcher(
            _storageDirectory!,
            e => ProcessFileEventAsync(watcher!, e),
            () => RunLockedAsync(() => ScanAsync(CancellationToken.None)),
            _logger);

        _fileWatcher = watcher;
        watcher.Start();
    }

    private Task ProcessFileEventAsync(StorageFileWatcher watcher, FileSystemEventArgs e)
        => RunLockedAsync(() =>
        {
            // Close over the watcher that raised the event rather than reading the mutable _fileWatcher
            // field, which a concurrent reconnect may have already reassigned or cleared.
            var relativePath = watcher.GetRelativePath(e.FullPath);

            return e.ChangeType switch
            {
                WatcherChangeTypes.Created => HandleFileCreatedAsync(relativePath),
                WatcherChangeTypes.Changed => HandleFileChangedAsync(relativePath, e.FullPath),
                WatcherChangeTypes.Deleted => HandleFileDeletedAsync(relativePath),
                WatcherChangeTypes.Renamed when e is RenamedEventArgs re =>
                    HandleFileRenamedAsync(relativePath, watcher.GetRelativePath(re.OldFullPath)),
                _ => Task.CompletedTask
            };
        });

    private async Task RunLockedAsync(Func<Task> action)
    {
        await _hierarchyLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _hierarchyLock.Release();
        }
    }

    private async Task HandleFileCreatedAsync(string relativePath)
    {
        _logger?.LogDebug("File created: {Path}", relativePath);

        var blob = new Blob(relativePath);
        var subject = await _subjectFactory.CreateFromBlobAsync(_client!, this, blob, CancellationToken.None);

        if (subject != null)
        {
            if (Path.GetExtension(relativePath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var content = await _client!.ReadTextAsync(relativePath);
                    _pathRegistry.UpdateHash(relativePath, StoragePathRegistry.ComputeHash(content));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to compute hash for: {Path}", relativePath);
                }
            }

            // Use reusable helper to add to hierarchy
            AddToHierarchy(relativePath, subject);

            _logger?.LogInformation("Added file from external: {Path}", relativePath);
        }
    }

    private async Task HandleFileChangedAsync(string relativePath, string fullPath)
    {
        if (!_pathRegistry.TryGetSubject(relativePath, out var existingSubject))
            return;

        // Checked before IStorageFile, which UnknownSubject also implements.
        if (existingSubject is UnknownSubject unknownSubject)
        {
            try
            {
                await RecreateAsync(relativePath, unknownSubject, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to recreate unknown subject: {Path}", relativePath);
            }
        }
        else if (existingSubject is IStorageFile storageFile)
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

    private Task HandleFileDeletedAsync(string relativePath)
    {
        _logger?.LogDebug("File deleted: {Path}", relativePath);

        if (!_pathRegistry.TryGetSubject(relativePath, out var subject))
        {
            _logger?.LogDebug("Subject not found for path: {Path}", relativePath);
            return Task.CompletedTask;
        }

        // Use reusable helper to remove from hierarchy
        RemoveFromHierarchy(relativePath, subject);

        _logger?.LogInformation("Removed deleted file: {Path}", relativePath);
        return Task.CompletedTask;
    }

    private async Task HandleFileRenamedAsync(string newPath, string oldPath)
    {
        await HandleFileDeletedAsync(oldPath);
        await HandleFileCreatedAsync(newPath);
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
    // TODO: Consider adding synchronization (lock) for thread safety if AddSubjectAsync/DeleteSubjectAsync can be called concurrently
    private void AddToHierarchy(string path, IInterceptorSubject subject)
    {
        var children = new Dictionary<string, IInterceptorSubject>(Children);

        _pathRegistry.Register(subject, path);
        _hierarchyManager.PlaceInHierarchy(path, subject, children, this);

        Children = children;
    }

    /// <summary>
    /// Removes a subject from the hierarchy. Reusable helper for delete operations and file watcher events.
    /// </summary>
    private void RemoveFromHierarchy(string path, IInterceptorSubject subject)
    {
        _pathRegistry.Unregister(path);

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        _hierarchyManager.RemoveFromHierarchy(path, subject, children);

        Children = children;
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

        // The own-write mark suppresses the watcher event, so the written subject is refreshed here.
        if (_pathRegistry.TryGetSubject(path, out var subject))
        {
            if (subject is UnknownSubject unknownSubject)
            {
                await RunLockedAsync(() => RecreateAsync(path, unknownSubject, cancellationToken));
            }
            else if (subject is IStorageFile file)
            {
                await file.OnFileChangedAsync(cancellationToken);
            }
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

    private void OnTypesChanged(object? sender, EventArgs e)
    {
        _ = UpgradeUnknownSubjectsAsync();
    }

    /// <summary>
    /// Recreates every <see cref="UnknownSubject"/> that has a type name from its file and replaces it when the result differs.
    /// </summary>
    internal async Task UpgradeUnknownSubjectsAsync()
    {
        try
        {
            await RunLockedAsync(async () =>
            {
                if (_client is null)
                {
                    return;
                }

                foreach (var (unknownSubject, path) in _pathRegistry.GetSubjects<UnknownSubject>())
                {
                    // Invalid JSON does not heal when types change; file changes and writes recreate it.
                    if (string.IsNullOrEmpty(unknownSubject.TypeName))
                    {
                        continue;
                    }

                    try
                    {
                        await RecreateAsync(path, unknownSubject, CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        _logger?.LogWarning(exception, "Failed to upgrade unknown subject: {Path}", path);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to upgrade unknown subjects in storage.");
        }
    }

    // Callers hold _hierarchyLock.
    private async Task RecreateAsync(string path, UnknownSubject unknownSubject, CancellationToken cancellationToken)
    {
        // Another swap may have replaced the placeholder while the caller waited for the lock.
        if (!_pathRegistry.TryGetSubject(path, out var currentSubject) || !ReferenceEquals(currentSubject, unknownSubject))
        {
            return;
        }

        var replacement = await _subjectFactory.CreateFromBlobAsync(Client, this, new Blob(path), cancellationToken);
        if (replacement is null ||
            replacement is UnknownSubject candidate &&
            candidate.TypeName == unknownSubject.TypeName &&
            candidate.Reason == unknownSubject.Reason)
        {
            // Unchanged placeholder: keep the instance so the UI does not churn, but refresh its file metadata.
            await unknownSubject.OnFileChangedAsync(cancellationToken);
            return;
        }

        string? hash = null;
        try
        {
            var content = await Client.ReadTextAsync(path, cancellationToken: cancellationToken);
            hash = StoragePathRegistry.ComputeHash(content);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to compute hash for: {Path}", path);
        }

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        _hierarchyManager.RemoveFromHierarchy(path, unknownSubject, children);
        _pathRegistry.Unregister(path);
        _pathRegistry.Register(replacement, path);
        if (hash is not null)
        {
            // After Register because Unregister drops the stored hash.
            _pathRegistry.UpdateHash(path, hash);
        }

        _hierarchyManager.PlaceInHierarchy(path, replacement, children, this);
        Children = children;

        _logger?.LogInformation("Recreated {Path} as {Type}.", path, replacement.GetType().FullName);
    }

    public override void Dispose()
    {
        if (_typeProvider is not null)
        {
            _typeProvider.TypesChanged -= OnTypesChanged;
        }

        _fileWatcher?.Dispose();
        _hierarchyLock.Dispose();
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
        base.Dispose();
    }
}

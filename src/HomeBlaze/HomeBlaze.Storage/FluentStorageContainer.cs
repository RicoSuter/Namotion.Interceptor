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
using Namotion.Interceptor.Tracking;

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

    // Serializes everything that swaps the client, the file watcher or children: connects, scans, file
    // watcher events, refreshes after writes, adds, deletes and placeholder upgrades. Configuration refreshes of subjects
    // (ApplyConfigurationAsync) run under it, so a slow apply delays the other storage events. Not
    // reentrant: code running under it must not call RunLockedAsync or the public write, add or delete
    // methods. Never disposed: it holds no resource, and disposing would break an in-flight Release.
    private readonly SemaphoreSlim _hierarchyLock = new(1, 1);

    // Checked under _hierarchyLock so no locked action starts after Dispose.
    private volatile bool _disposed;

    // 1 while an upgrade pass is queued but has not yet read the path registry.
    private int _upgradePending;

    private StorageFileWatcher? _fileWatcher;
    private string? _storageDirectory;
    private JsonSubjectSynchronizer? _jsonSyncHelper;

    // What the client lists, and what the last completed scan listed. A scan reconciles the hierarchy only when
    // both are the same storage: the files of another storage are not the files the current subjects stand for.
    private StorageLocation? _clientLocation;
    private StorageLocation? _scannedLocation;

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
        
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Taken before base.StartAsync while the hosted service start still defers startup, so startup cannot
        // complete in between. Held until the first scan attached the files.
        var startupDeferral = ((IInterceptorSubject)this).Context.DeferStartupCompletion();
        try
        {
            await base.StartAsync(cancellationToken);
        }
        catch
        {
            startupDeferral.Dispose();
            throw;
        }

        // Bound to this start's ExecuteAsync, so an earlier start's ExecuteAsync ending late cannot release it.
        startupDeferral.ReleaseWhenCompleted(ExecuteTask);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConnectAsync(stoppingToken);

        var context = ((IInterceptorSubject)this).Context;
        if (!context.IsStartupCompleted())
        {
            // Not awaited: startup waits for this ExecuteAsync to finish.
            _ = WarnAboutMissingSubjectBlockTypesAfterStartupAsync(context, stoppingToken);
        }
    }

    /// <summary>
    /// Warns about markdown subject blocks whose type is still not loaded once startup completed. While it runs,
    /// the parser only logs them as information because plugins may still add the types.
    /// </summary>
    private async Task WarnAboutMissingSubjectBlockTypesAfterStartupAsync(
        IInterceptorSubjectContext context, CancellationToken stoppingToken)
    {
        try
        {
            await context.WaitForStartupAsync(stoppingToken);
            await RunLockedAsync(() =>
            {
                foreach (var (markdownFile, path) in _pathRegistry.GetSubjects<MarkdownFile>())
                {
                    if (markdownFile.UnresolvedSubjectTypeNames.Count > 0)
                    {
                        _logger?.LogWarning(
                            "Subject blocks in {Path} are not shown because their types {Types} are still not loaded after startup completed.",
                            path, string.Join(", ", markdownFile.UnresolvedSubjectTypeNames));
                    }
                }

                return Task.CompletedTask;
            }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped before startup completed: nothing to report.
        }
        catch (Exception exception)
        {
            // Includes a failed startup, which the root manager reports itself.
            _logger?.LogDebug(exception, "Skipped reporting markdown subject blocks with missing types.");
        }
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
            await RunLockedAsync(async () =>
            {
                // Subscribing before the scan means a type added during the scan is not missed: its upgrade
                // waits for the lock and then sees the scanned placeholders.
                SubscribeToTypeChanges();

                _storageDirectory = isInMemory ? null : ResolveStorageDirectory();

                var storageType = StorageType;
                var previousClient = _client;
                _client = storageType switch
                {
                    "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
                    "inmemory" => StorageFactory.Blobs.InMemory(),
                    _ => throw new NotSupportedException($"Storage type '{storageType}' is not supported")
                };
                _clientLocation = new StorageLocation(
                    storageType == "filesystem" ? "disk" : storageType,
                    _storageDirectory is null ? null : Path.TrimEndingDirectorySeparator(_storageDirectory));
                previousClient?.Dispose();

                _jsonSyncHelper = new JsonSubjectSynchronizer(_pathRegistry, _serializer, _client, _logger);

                Status = StorageStatus.Connected;
                _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                    isInMemory ? "(in-memory)" : _storageDirectory);

                await ScanAsync(cancellationToken);

                if (EnableFileWatching && !isInMemory)
                {
                    StartFileWatching();
                }
                else
                {
                    Interlocked.Exchange(ref _fileWatcher, null)?.Dispose();
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Status = StorageStatus.Error;
            _logger?.LogError(ex, "Failed to connect to storage");
            throw;
        }
    }

    /// <summary>
    /// Lists the storage and publishes its subject hierarchy. Callers hold the hierarchy lock. When the last
    /// completed scan listed the same storage, the hierarchy is reconciled: a listed file keeps its subject, which
    /// takes changed content in place (see <see cref="ReconcileFileAsync"/>), a listed folder keeps its
    /// <see cref="VirtualFolder"/>, and only the subjects of other files are created. Otherwise every subject is created.
    /// </summary>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var location = _clientLocation;
        var reconcile = location == _scannedLocation;
        _logger?.LogInformation(reconcile ? "Rescanning storage..." : "Scanning storage...");

        var blobs = await Client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        // By the registered path with its case: the registry's own lookup ignores case, so on a case-sensitive file
        // system it would give two files whose names differ only in case the same subject.
        Dictionary<string, IInterceptorSubject>? registeredSubjects = null;
        if (reconcile)
        {
            registeredSubjects = new Dictionary<string, IInterceptorSubject>(_pathRegistry.Count, StringComparer.Ordinal);
            foreach (var (subject, path) in _pathRegistry.GetSubjects<IInterceptorSubject>())
            {
                registeredSubjects[path] = subject;
            }
        }

        var entries = new List<(string Path, IInterceptorSubject? Subject)>(blobs.Count);
        var hashes = new List<(string Path, string Hash)>();
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
                entries.Add((blob.FullPath, null));
                continue;
            }

            try
            {
                var (subject, hash) = registeredSubjects is not null &&
                    registeredSubjects.TryGetValue(StoragePathRegistry.NormalizePath(blob.FullPath), out var existingSubject)
                    ? await ReconcileFileAsync(blob, existingSubject, cancellationToken)
                    : await CreateFileSubjectAsync(blob, cancellationToken);

                if (subject != null)
                {
                    entries.Add((blob.FullPath, subject));
                    if (hash != null)
                    {
                        hashes.Add((blob.FullPath, hash));
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Failed to create subject for blob: {Path}", blob.FullPath);
            }
        }

        // A cancelled scan must not publish: the files it did not reach would lose their subjects.
        cancellationToken.ThrowIfCancellationRequested();
        PublishScan(entries, hashes, reconcile);
        _scannedLocation = location;
        _logger?.LogInformation("Scan complete: Found {Count} subjects.", _pathRegistry.Count);
    }

    /// <summary>
    /// Returns the subject for a listed file that has <paramref name="subject"/>, and the hash to store when it
    /// changed. Unchanged content (same JSON hash, or same size and modification time) keeps the subject as is.
    /// Changed content is taken in place as a file watcher change would, unless the file now needs a subject of
    /// another type, which is created. A placeholder tries its type again, as a rebuild would.
    /// </summary>
    private async Task<(IInterceptorSubject? Subject, string? Hash)> ReconcileFileAsync(
        Blob blob, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var path = blob.FullPath;
        var fullPath = GetFileSystemPath(path);

        // The writer refreshes the subject itself, and the file may not hold the written content yet.
        if (_fileWatcher is { } fileWatcher && fileWatcher.IsOwnWrite(fullPath))
        {
            return (subject, null);
        }

        try
        {
            if (subject is UnknownSubject unknownSubject)
            {
                var replacement = await CreatePlaceholderReplacementAsync(blob, unknownSubject, cancellationToken);
                return (replacement ?? unknownSubject, await TryReadHashAsync(path, cancellationToken));
            }

            if (IsJsonPath(path))
            {
                var json = await Client.ReadTextAsync(path, cancellationToken: cancellationToken);
                var hash = StoragePathRegistry.ComputeHash(json);
                if (!_pathRegistry.HasHashChanged(path, hash))
                {
                    return (subject, null);
                }

                if (!_subjectFactory.CreatesSameType(subject, path, json))
                {
                    return await CreateFileSubjectAsync(blob, cancellationToken);
                }

                await RefreshSubjectAsync(subject, path, fullPath);

                // The synchronizer stores the hash of the content it applied to a configurable subject.
                return (subject, subject is IConfigurable ? null : hash);
            }

            if (!_subjectFactory.CreatesSameType(subject, path, null))
            {
                return await CreateFileSubjectAsync(blob, cancellationToken);
            }

            if (subject is IStorageFile file &&
                (file.FileSize != (blob.Size ?? 0L) || file.LastModified != blob.LastModificationTime?.UtcDateTime))
            {
                await RefreshSubjectAsync(subject, path, fullPath);
            }

            return (subject, null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(exception, "Failed to reconcile {Path}, keeping its subject.", path);
            return (subject, null);
        }
    }

    private async Task<(IInterceptorSubject? Subject, string? Hash)> CreateFileSubjectAsync(
        Blob blob, CancellationToken cancellationToken)
    {
        var subject = await _subjectFactory.CreateFromBlobAsync(Client, this, blob, cancellationToken);
        var hash = subject != null && IsJsonPath(blob.FullPath)
            ? await TryReadHashAsync(blob.FullPath, cancellationToken)
            : null;

        return (subject, hash);
    }

    /// <summary>
    /// Registers the scanned subjects and publishes their hierarchy. With <paramref name="reconcile"/>, subjects that
    /// are no longer listed are unregistered and the current hierarchy's folders are reused; otherwise the registry
    /// and hierarchy start empty. Callers hold the hierarchy lock.
    /// </summary>
    private void PublishScan(
        List<(string Path, IInterceptorSubject? Subject)> entries,
        List<(string Path, string Hash)> hashes,
        bool reconcile)
    {
        // No await from here on: a subject is attached as soon as its folder's children are assigned, and must be
        // registered by then.
        if (reconcile)
        {
            var listedSubjects = new HashSet<IInterceptorSubject>(entries.Count, ReferenceEqualityComparer.Instance);
            foreach (var (_, subject) in entries)
            {
                if (subject != null)
                {
                    listedSubjects.Add(subject);
                }
            }

            foreach (var (subject, path) in _pathRegistry.GetSubjects<IInterceptorSubject>())
            {
                if (!listedSubjects.Contains(subject))
                {
                    _pathRegistry.Unregister(subject);
                }
            }
        }
        else
        {
            _pathRegistry.Clear();
        }

        foreach (var (path, subject) in entries)
        {
            if (subject != null && !_pathRegistry.TryGetPath(subject, out _))
            {
                _pathRegistry.Register(subject, path);
            }
        }

        foreach (var (path, hash) in hashes)
        {
            _pathRegistry.UpdateHash(path, hash);
        }

        var children = _hierarchyManager.BuildHierarchy(
            entries, reconcile ? Children : new Dictionary<string, IInterceptorSubject>(), this);

        if (!ReferenceEquals(children, Children))
        {
            Children = children;
        }
    }

    // Callers hold _hierarchyLock.
    private async Task<string?> TryReadHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var content = await Client.ReadTextAsync(path, cancellationToken: cancellationToken);
            return StoragePathRegistry.ComputeHash(content);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(exception, "Failed to compute hash for: {Path}", path);
            return null;
        }
    }

    private static bool IsJsonPath(string path)
        => Path.GetExtension(path).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);

    // Callers hold _hierarchyLock.
    private void StartFileWatching()
    {
        StorageFileWatcher? watcher = null;
        watcher = new StorageFileWatcher(
            _storageDirectory!,
            e => ProcessFileEventAsync(watcher!, e),
            () => RunLockedAsync(() => ScanAsync(CancellationToken.None)),
            _logger);

        // Exchanged rather than assigned because StopAsync clears the field without the lock.
        Interlocked.Exchange(ref _fileWatcher, watcher)?.Dispose();
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

    private async Task RunLockedAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await _hierarchyLock.WaitAsync(cancellationToken);
        try
        {
            if (!_disposed)
            {
                await action();
            }
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
            if (IsJsonPath(relativePath) && await TryReadHashAsync(relativePath, CancellationToken.None) is { } hash)
            {
                _pathRegistry.UpdateHash(relativePath, hash);
            }

            // Use reusable helper to add to hierarchy
            AddToHierarchy(relativePath, subject);

            _logger?.LogInformation("Added file from external: {Path}", relativePath);
        }
    }

    private async Task HandleFileChangedAsync(string relativePath, string fullPath)
    {
        if (_pathRegistry.TryGetSubject(relativePath, out var existingSubject))
        {
            await RefreshSubjectAsync(existingSubject, relativePath, fullPath);
        }
    }

    /// <summary>
    /// Makes the subject of a changed file take its content. Callers hold the hierarchy lock.
    /// </summary>
    private async Task RefreshSubjectAsync(IInterceptorSubject existingSubject, string relativePath, string fullPath)
    {
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
    public Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
        => RunLockedAsync(async () =>
        {
            var fullPath = GetFileSystemPath(path);
            _fileWatcher?.MarkAsOwnWrite(fullPath);

            var json = _subjectFactory.Serialize(subject);
            _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(json));

            await Client.WriteTextAsync(path, json, cancellationToken: cancellationToken);

            // Update hierarchy - extracted to reusable method
            AddToHierarchy(path, subject);

            _logger?.LogInformation("Added subject to storage: {Path}", path);
        }, cancellationToken);

    /// <summary>
    /// Adds a subject to the hierarchy. Reusable helper for AddSubjectAsync and file watcher events.
    /// Callers hold the hierarchy lock.
    /// </summary>
    private void AddToHierarchy(string path, IInterceptorSubject subject)
    {
        var children = new Dictionary<string, IInterceptorSubject>(Children);

        _pathRegistry.Register(subject, path);
        _hierarchyManager.PlaceInHierarchy(path, subject, children, this);

        Children = children;
    }

    /// <summary>
    /// Removes a subject from the hierarchy. Reusable helper for delete operations and file watcher events.
    /// Callers hold the hierarchy lock.
    /// </summary>
    private void RemoveFromHierarchy(string path, IInterceptorSubject subject)
    {
        _pathRegistry.Unregister(subject);

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

        // The own-write mark suppresses the watcher event, so the subject now registered at the path is
        // refreshed here. It may not be the caller: a placeholder upgraded meanwhile stays in use by the UI.
        await RunLockedAsync(() => HandleFileChangedAsync(path, fullPath), cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage and removes from Children.
    /// </summary>
    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
        => RunLockedAsync(async () =>
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
        }, cancellationToken);

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

    private void SubscribeToTypeChanges()
    {
        if (_typeProvider is not null)
        {
            // Removing first keeps a reconnect from subscribing twice.
            _typeProvider.TypesChanged -= OnTypesChanged;
            _typeProvider.TypesChanged += OnTypesChanged;

            // Dispose does not take the lock, so it may have unsubscribed just before the line above.
            if (_disposed)
            {
                _typeProvider.TypesChanged -= OnTypesChanged;
            }
        }
    }

    private void UnsubscribeFromTypeChanges()
    {
        if (_typeProvider is not null)
        {
            _typeProvider.TypesChanged -= OnTypesChanged;
        }
    }

    private void OnTypesChanged(object? sender, EventArgs e)
    {
        // Raised on the thread adding the types, so the pass runs elsewhere. A pass that has not yet read
        // the registry also covers these types, so no second one is queued.
        if (Interlocked.Exchange(ref _upgradePending, 1) == 0)
        {
            // Deferred here, synchronously, while the provider that added the types still defers startup, so
            // startup completes only once the real subjects replaced their placeholders.
            var startupDeferral = ((IInterceptorSubject)this).Context.DeferStartupCompletion();
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpgradeUnknownSubjectsAsync();
                }
                finally
                {
                    startupDeferral.Dispose();
                }
            });
        }
    }

    /// <summary>
    /// Recreates every <see cref="UnknownSubject"/> that has a type name from its file and replaces it when the result differs,
    /// and parses again every <see cref="MarkdownFile"/> with a subject block whose type is now loaded.
    /// </summary>
    internal async Task UpgradeUnknownSubjectsAsync()
    {
        try
        {
            await RunLockedAsync(async () =>
            {
                // Cleared before reading the registry so types added during this pass queue another one.
                Interlocked.Exchange(ref _upgradePending, 0);

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

                foreach (var (markdownFile, path) in _pathRegistry.GetSubjects<MarkdownFile>())
                {
                    if (!markdownFile.UnresolvedSubjectTypeNames.Any(IsTypeLoaded))
                    {
                        continue;
                    }

                    try
                    {
                        // Parsing again adds the subject blocks that were skipped while their type was missing.
                        await markdownFile.OnFileChangedAsync(CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        _logger?.LogWarning(exception, "Failed to refresh markdown file: {Path}", path);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to upgrade unknown subjects in storage.");
        }
    }

    private bool IsTypeLoaded(string typeName)
    {
        return _typeProvider is null || _typeProvider.TryGetType(typeName, out _);
    }

    // Callers hold _hierarchyLock.
    private async Task RecreateAsync(string path, UnknownSubject unknownSubject, CancellationToken cancellationToken)
    {
        // Another swap may have replaced the placeholder while the caller waited for the lock.
        if (!_pathRegistry.TryGetSubject(path, out var currentSubject) || !ReferenceEquals(currentSubject, unknownSubject))
        {
            return;
        }

        var replacement = await CreatePlaceholderReplacementAsync(new Blob(path), unknownSubject, cancellationToken);
        if (replacement is null)
        {
            return;
        }

        // Read before the swap: a nested replacement is attached as soon as its folder's children are assigned,
        // so no await may separate that from the registry update.
        var hash = await TryReadHashAsync(path, cancellationToken);

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        if (!_hierarchyManager.ReplaceInHierarchy(path, unknownSubject, replacement, children))
        {
            // The placeholder lost its key to another entry when it was placed, so it has no place to take.
            _logger?.LogDebug("Skipping recreation of {Path}: the placeholder is not in the hierarchy.", path);
            return;
        }

        _pathRegistry.Unregister(unknownSubject);
        _pathRegistry.Register(replacement, path);
        if (hash is not null)
        {
            // After Register because Unregister drops the stored hash.
            _pathRegistry.UpdateHash(path, hash);
        }

        Children = children;

        _logger?.LogInformation("Recreated {Path} as {Type}.", path, replacement.GetType().FullName);
    }

    /// <summary>
    /// Creates the subject of a placeholder's file. Returns null when the file still gives a placeholder, whose type
    /// name and reason <paramref name="unknownSubject"/> then takes over, so the UI does not churn. Callers hold the
    /// hierarchy lock.
    /// </summary>
    private async Task<IInterceptorSubject?> CreatePlaceholderReplacementAsync(
        Blob blob, UnknownSubject unknownSubject, CancellationToken cancellationToken)
    {
        var replacement = await _subjectFactory.CreateFromBlobAsync(Client, this, blob, cancellationToken);
        if (replacement is not (null or UnknownSubject))
        {
            return replacement;
        }

        if (replacement is UnknownSubject candidate)
        {
            unknownSubject.TypeName = candidate.TypeName;
            unknownSubject.Reason = candidate.Reason;
        }

        await unknownSubject.OnFileChangedAsync(cancellationToken);
        return null;
    }

    private readonly record struct StorageLocation(string Type, string? Directory);

    /// <summary>
    /// Stops reacting to type and file changes. A later start connects again.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Without the lock: a detached storage is stopped from the host's queue, which must not wait on a
        // long-running storage event.
        UnsubscribeFromTypeChanges();
        Interlocked.Exchange(ref _fileWatcher, null)?.Dispose();

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _disposed = true;
        UnsubscribeFromTypeChanges();

        Interlocked.Exchange(ref _fileWatcher, null)?.Dispose();
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
        base.Dispose();
    }
}

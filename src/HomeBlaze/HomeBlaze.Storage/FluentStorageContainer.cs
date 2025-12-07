using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage root using FluentStorage. Implements IStorageContainer and IConfigurationWriter.
/// Supports FileSystemWatcher for reactive file monitoring on disk storage.
/// </summary>
[InterceptorSubject]
public partial class FluentStorageContainer : 
    BackgroundService,
    IStorageContainer, IConfigurationWriter, IDisplaySubject, IConfigurableSubject, IDisposable
{
    private const string StorageIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M2,20H22V16H2V20M4,17H6V19H4V17M2,4V8H22V4H2M6,7H4V5H6V7M2,14H22V10H2V14M4,11H6V13H4V11Z\" /></svg>";

    private IBlobStorage? _client;

    private readonly StoragePathRegistry _pathRegistry = new();
    private readonly FileSubjectFactory _subjectFactory;
    private readonly StorageHierarchyManager _hierarchyManager;
    private StorageFileWatcher? _fileWatcher;

    private readonly ILogger<FluentStorageContainer>? _logger;

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

    public string Icon => StorageIcon;

    public FluentStorageContainer(
        SubjectTypeRegistry typeRegistry,
        ConfigurableSubjectSerializer serializer,
        ILogger<FluentStorageContainer>? logger = null)
    {
        _subjectFactory = new FileSubjectFactory(typeRegistry, serializer, logger);
        _hierarchyManager = new StorageHierarchyManager(logger);
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
    /// IConfigurableSubject implementation - called after configuration properties are updated.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken ct = default)
    {
        // Reconnect if configuration changed
        return ConnectAsync(ct);
    }

    /// <summary>
    /// Initializes the storage client based on configuration.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        Status = StorageStatus.Initializing;

        try
        {
            _client = StorageType.ToLowerInvariant() switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(
                    Path.GetFullPath(ConnectionString)),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };

            Status = StorageStatus.Connected;
            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType, ConnectionString);

            await ScanAsync(ct);

            if (EnableFileWatching && StorageType.ToLowerInvariant() is "disk" or "filesystem")
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
    public async Task ScanAsync(CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        _logger?.LogInformation("Scanning storage...");

        var blobs = await _client.ListAsync(recurse: true, cancellationToken: ct);

        _pathRegistry.Clear();
        var children = new Dictionary<string, IInterceptorSubject>();
        var context = ((IInterceptorSubject)this).Context;

        foreach (var blob in blobs.Where(b => !b.IsFolder))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var subject = await _subjectFactory.CreateFromBlobAsync(_client, this, blob, context, ct);
                if (subject != null)
                {
                    _hierarchyManager.PlaceInHierarchy(blob.FullPath, subject, children, context, this);
                    _pathRegistry.Register(subject, blob.FullPath);

                    if (Path.GetExtension(blob.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var content = await _client.ReadTextAsync(blob.FullPath, cancellationToken: ct);
                            _pathRegistry.UpdateHash(blob.FullPath, StoragePathRegistry.ComputeHash(content));
                        }
                        catch { /* Ignore hash computation errors */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create subject for blob: {Path}", blob.FullPath);
            }
        }

        Children = children;
        _logger?.LogInformation("Scan complete. Found {Count} subjects", _pathRegistry.Count);
    }

    private void StartFileWatching()
    {
        _fileWatcher = new StorageFileWatcher(
            Path.GetFullPath(ConnectionString),
            ProcessFileEventAsync,
            () => ScanAsync(CancellationToken.None),
            _logger);
        _fileWatcher.Start();
    }

    private Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        var relativePath = _fileWatcher!.GetRelativePath(e.FullPath);

        return e.ChangeType switch
        {
            WatcherChangeTypes.Created => HandleFileCreatedAsync(relativePath),
            WatcherChangeTypes.Changed => HandleFileChangedAsync(relativePath, e.FullPath),
            WatcherChangeTypes.Deleted => HandleFileDeletedAsync(relativePath),
            WatcherChangeTypes.Renamed when e is RenamedEventArgs re =>
                HandleFileRenamedAsync(relativePath, _fileWatcher.GetRelativePath(re.OldFullPath)),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleFileCreatedAsync(string relativePath)
    {
        _logger?.LogDebug("File created: {Path}", relativePath);

        var blob = new Blob(relativePath);
        var context = ((IInterceptorSubject)this).Context;
        var subject = await _subjectFactory.CreateFromBlobAsync(_client!, this, blob, context, CancellationToken.None);

        if (subject != null)
        {
            if (Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var content = await _client!.ReadTextAsync(relativePath);
                    _pathRegistry.UpdateHash(relativePath, StoragePathRegistry.ComputeHash(content));
                }
                catch { /* Ignore */ }
            }

            _pathRegistry.Register(subject, relativePath);

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            _hierarchyManager.PlaceInHierarchy(relativePath, subject, children, context, this);
            Children = children;

            _logger?.LogInformation("Added file from external: {Path}", relativePath);
        }
    }

    private async Task HandleFileChangedAsync(string relativePath, string fullPath)
    {
        if (!_pathRegistry.TryGetSubject(relativePath, out var existingSubject))
            return;

        if (existingSubject is not IConfigurableSubject)
            return;

        long newSize;
        try
        {
            var fileInfo = new FileInfo(fullPath);
            newSize = fileInfo.Length;
        }
        catch
        {
            return;
        }

        bool sizeChanged = _pathRegistry.HasSizeChanged(relativePath, newSize);

        string? newHash = null;
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                using var stream = await _client!.OpenReadAsync(relativePath);
                newHash = await StoragePathRegistry.ComputeHashAsync(stream);
                break;
            }
            catch (IOException) when (retry < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, retry)));
            }
        }

        if (newHash == null)
        {
            _logger?.LogWarning("Could not compute hash after retries: {Path}", relativePath);
            return;
        }

        if (!sizeChanged && !_pathRegistry.HasHashChanged(relativePath, newHash))
        {
            _logger?.LogDebug("File unchanged (same hash), skipping reload: {Path}", relativePath);
            return;
        }

        _pathRegistry.UpdateSize(relativePath, newSize);
        _pathRegistry.UpdateHash(relativePath, newHash);

        if (Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var json = await _client!.ReadTextAsync(relativePath);
                await _subjectFactory.UpdateFromJsonAsync(existingSubject, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to update from JSON: {Path}", relativePath);
            }
        }
        else if (existingSubject is IConfigurableSubject configurable)
        {
            await configurable.ApplyConfigurationAsync();
        }

        _logger?.LogInformation("Reloaded: {Path}", relativePath);
    }

    private Task HandleFileDeletedAsync(string relativePath)
    {
        _logger?.LogDebug("File deleted: {Path}", relativePath);

        _pathRegistry.Unregister(relativePath);

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        _hierarchyManager.RemoveFromHierarchy(relativePath, children);
        Children = children;

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
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (_client == null)
            return false;

        if (!_pathRegistry.TryGetPath(subject, out var path))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        var json = _subjectFactory.Serialize(subject);
        _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(json));

        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        _logger?.LogDebug("Saved subject to storage: {Path}", path);
        return true;
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        var json = _subjectFactory.Serialize(subject);
        _pathRegistry.UpdateHash(path, StoragePathRegistry.ComputeHash(json));

        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        var context = ((IInterceptorSubject)this).Context;
        var children = new Dictionary<string, IInterceptorSubject>(Children);

        _pathRegistry.Register(subject, path);
        _hierarchyManager.PlaceInHierarchy(path, subject, children, context, this);

        Children = children;

        _logger?.LogInformation("Added subject to storage: {Path}", path);
    }

    /// <summary>
    /// Deletes a subject from storage.
    /// </summary>
    public async Task DeleteSubjectAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        await _client.DeleteAsync(path, cancellationToken: ct);

        _pathRegistry.Unregister(path);

        _logger?.LogInformation("Deleted from storage: {Path}", path);
    }

    /// <summary>
    /// IStorageContainer - Reads a blob from storage.
    /// </summary>
    public async Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        return await _client.OpenReadAsync(path, cancellationToken: ct);
    }

    /// <summary>
    /// IStorageContainer - Writes a blob to storage.
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        await _client.WriteAsync(path, content, append: false, cancellationToken: ct);
        _logger?.LogDebug("Wrote blob to storage: {Path}", path);
    }

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        _fileWatcher?.MarkAsOwnWrite(fullPath);

        await _client.DeleteAsync(path, cancellationToken: ct);
        _logger?.LogDebug("Deleted blob from storage: {Path}", path);
    }

    /// <summary>
    /// Reads blob text content from storage.
    /// </summary>
    public async Task<string> ReadBlobTextAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        return await _client.ReadTextAsync(path, cancellationToken: ct);
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

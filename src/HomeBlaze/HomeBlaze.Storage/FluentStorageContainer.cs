using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core.Extensions;
using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage root using FluentStorage. Implements ISubjectStorageHandler.
/// Supports FileSystemWatcher for reactive file monitoring on disk storage.
/// </summary>
[InterceptorSubject]
public partial class FluentStorageContainer : ISubjectStorageHandler, ITitleProvider, IIconProvider, IDisposable
{
    // MudBlazor Icons.Material.Filled.Storage
    private const string StorageIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M2,20H22V16H2V20M4,17H6V19H4V17M2,4V8H22V4H2M6,7H4V5H6V7M2,14H22V10H2V14M4,11H6V13H4V11Z\" /></svg>";

    private IBlobStorage? _client;

    // Thread-safe: accessed from StorageService background thread and UI thread
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();
    // Reverse lookup for O(1) path-to-subject resolution
    private readonly ConcurrentDictionary<string, IInterceptorSubject> _pathToSubject = new();
    // Content hashes for change detection (path -> SHA256 hash)
    private readonly ConcurrentDictionary<string, string> _contentHashes = new();
    // Self-write tracking to prevent FileSystemWatcher feedback loops
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingWrites = new();

    // FileSystemWatcher and Rx processing
    private FileSystemWatcher? _watcher;
    private readonly Subject<FileSystemEventArgs> _fileEvents = new();
    private IDisposable? _fileEventSubscription;

    private static readonly TimeSpan WriteGracePeriod = TimeSpan.FromSeconds(2);

    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly ILogger<FluentStorageContainer>? _logger;

    /// <summary>
    /// Storage type identifier (e.g., "disk", "azure-blob").
    /// </summary>
    [Configuration]
    public partial string StorageType { get; set; }

    /// <summary>
    /// Connection string or path for the storage.
    /// For disk: absolute or relative path.
    /// For cloud: connection string.
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
    /// Only applies to disk storage.
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

    public string? Title => string.IsNullOrEmpty(ConnectionString)
        ? "Storage"
        : Path.GetFileName(ConnectionString.TrimEnd('/', '\\'));

    public string Icon => StorageIcon;

    public FluentStorageContainer(
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
        ILogger<FluentStorageContainer>? logger = null)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _logger = logger;

        StorageType = "disk";
        ConnectionString = string.Empty;
        EnableFileWatching = true;
        Children = new Dictionary<string, IInterceptorSubject>();
        Status = StorageStatus.Disconnected;
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

            // Start file watching if enabled and using disk storage
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

        // Clear existing mappings
        _subjectPaths.Clear();
        _pathToSubject.Clear();
        _contentHashes.Clear();

        var children = new Dictionary<string, IInterceptorSubject>();
        foreach (var blob in blobs.Where(b => !b.IsFolder))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var subject = await CreateSubjectFromBlobAsync(blob, ct);
                if (subject != null)
                {
                    PlaceInHierarchy(blob.FullPath, subject, children);
                    _subjectPaths[subject] = blob.FullPath;
                    _pathToSubject[blob.FullPath] = subject;

                    // Compute initial hashes for JSON files
                    if (Path.GetExtension(blob.FullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var content = await _client.ReadTextAsync(blob.FullPath, cancellationToken: ct);
                            _contentHashes[blob.FullPath] = ComputeHash(content);
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
        _logger?.LogInformation("Scan complete. Found {Count} subjects", _subjectPaths.Count);
    }

    #region FileSystemWatcher

    private void StartFileWatching()
    {
        var fullPath = Path.GetFullPath(ConnectionString);

        _watcher = new FileSystemWatcher(fullPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // 64KB buffer to reduce overflow risk
            EnableRaisingEvents = true
        };

        // Route all events to the Rx subject
        _watcher.Created += (s, e) => _fileEvents.OnNext(e);
        _watcher.Changed += (s, e) => _fileEvents.OnNext(e);
        _watcher.Deleted += (s, e) => _fileEvents.OnNext(e);
        _watcher.Renamed += (s, e) => _fileEvents.OnNext(e);
        _watcher.Error += OnWatcherError;

        // Process events with proper debouncing using System.Reactive
        _fileEventSubscription = _fileEvents
            .GroupBy(e => e.FullPath)
            .SelectMany(group => group.Throttle(TimeSpan.FromSeconds(1)))
            .Where(e => !IsOwnWrite(e.FullPath))
            .Subscribe(
                onNext: async e =>
                {
                    try
                    {
                        await ProcessFileEventAsync(e);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error processing file event: {Path} ({Type})",
                            e.FullPath, e.ChangeType);
                    }
                },
                onError: ex => _logger?.LogError(ex, "Error in file event stream"));

        _logger?.LogInformation("File watching enabled for: {Path}", fullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), triggering rescan");

        // Restart watcher
        _watcher?.Dispose();
        if (EnableFileWatching && Status == StorageStatus.Connected)
        {
            StartFileWatching();

            // Trigger full rescan to catch any missed events
            _ = Task.Run(async () =>
            {
                try
                {
                    await ScanAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to rescan after watcher error");
                }
            });
        }
    }

    private async Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        var relativePath = GetRelativePath(e.FullPath);

        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Created:
                await HandleFileCreatedAsync(relativePath, e.FullPath);
                break;

            case WatcherChangeTypes.Changed:
                await HandleFileChangedAsync(relativePath, e.FullPath);
                break;

            case WatcherChangeTypes.Deleted:
                HandleFileDeleted(relativePath);
                break;

            case WatcherChangeTypes.Renamed when e is RenamedEventArgs re:
                var oldRelativePath = GetRelativePath(re.OldFullPath);
                HandleFileDeleted(oldRelativePath);
                await HandleFileCreatedAsync(relativePath, e.FullPath);
                break;
        }
    }

    private async Task HandleFileCreatedAsync(string relativePath, string fullPath)
    {
        _logger?.LogDebug("File created: {Path}", relativePath);

        var blob = new Blob(relativePath);
        var subject = await CreateSubjectFromBlobAsync(blob, CancellationToken.None);

        if (subject != null)
        {
            // Compute content hash for JSON files
            if (Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var content = await _client!.ReadTextAsync(relativePath);
                    _contentHashes[relativePath] = ComputeHash(content);
                }
                catch { /* Non-text files won't have hash */ }
            }

            _pathToSubject[relativePath] = subject;
            _subjectPaths[subject] = relativePath;

            var children = new Dictionary<string, IInterceptorSubject>(Children);
            PlaceInHierarchy(relativePath, subject, children);
            Children = children;

            _logger?.LogInformation("Added file from external: {Path}", relativePath);
        }
    }

    private async Task HandleFileChangedAsync(string relativePath, string fullPath)
    {
        // Check if we have this subject
        if (!_pathToSubject.TryGetValue(relativePath, out var existingSubject))
            return;

        // Read new content with retry (file may still be locked)
        string? newJson = null;
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                newJson = await _client!.ReadTextAsync(relativePath);
                break;
            }
            catch (IOException) when (retry < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, retry)));
            }
        }

        if (newJson == null)
        {
            _logger?.LogWarning("Could not read file after retries: {Path}", relativePath);
            return;
        }

        // Check if content actually changed via hash comparison
        var newHash = ComputeHash(newJson);
        if (_contentHashes.TryGetValue(relativePath, out var oldHash) && oldHash == newHash)
        {
            _logger?.LogDebug("File unchanged (same hash), skipping reload: {Path}", relativePath);
            return;
        }

        _contentHashes[relativePath] = newHash;

        // If subject implements IReloadableConfiguration, update in place
        if (existingSubject is IReloadableConfiguration reloadable)
        {
            await reloadable.ReloadConfigurationAsync(newJson);
            _logger?.LogInformation("Reloaded configuration for: {Path}", relativePath);
        }
        else
        {
            // Fallback: deserialize and copy [Configuration] properties
            await ReloadConfigurationPropertiesAsync(existingSubject, newJson);
            _logger?.LogInformation("Updated configuration properties for: {Path}", relativePath);
        }
    }

    private async Task ReloadConfigurationPropertiesAsync(IInterceptorSubject existing, string json)
    {
        // Deserialize to temporary subject
        var context = ((IInterceptorSubject)this).Context;
        var tempSubject = _serializer.Deserialize(json, context);
        if (tempSubject == null)
            return;

        // Copy only [Configuration] properties from temp to existing
        foreach (var prop in existing.GetConfigurationProperties())
        {
            var tempRegistered = tempSubject.TryGetRegisteredSubject();
            var tempProp = tempRegistered?.TryGetProperty(prop.Name);
            if (tempProp != null)
            {
                var newValue = tempProp.GetValue();
                var oldValue = prop.GetValue();
                if (!Equals(newValue, oldValue))
                {
                    prop.SetValue(newValue);
                }
            }
        }

        await Task.CompletedTask; // For async signature consistency
    }

    private void HandleFileDeleted(string relativePath)
    {
        _logger?.LogDebug("File deleted: {Path}", relativePath);

        if (_pathToSubject.TryRemove(relativePath, out var subject))
        {
            _subjectPaths.TryRemove(subject, out _);
        }
        _contentHashes.TryRemove(relativePath, out _);

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        RemoveFromHierarchy(relativePath, children);
        Children = children;

        _logger?.LogInformation("Removed deleted file: {Path}", relativePath);
    }

    /// <summary>
    /// Marks a path as being written by us (prevents FileSystemWatcher feedback loop).
    /// </summary>
    private void MarkAsOwnWrite(string fullPath)
    {
        _pendingWrites[fullPath] = DateTimeOffset.UtcNow;

        // Schedule cleanup after grace period
        _ = Task.Run(async () =>
        {
            await Task.Delay(WriteGracePeriod);
            _pendingWrites.TryRemove(fullPath, out _);
        });
    }

    private bool IsOwnWrite(string fullPath)
    {
        if (_pendingWrites.TryGetValue(fullPath, out var writeTime))
        {
            return DateTimeOffset.UtcNow - writeTime < WriteGracePeriod;
        }
        return false;
    }

    private string GetRelativePath(string fullPath)
    {
        var basePath = Path.GetFullPath(ConnectionString);
        return Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    #endregion

    /// <summary>
    /// ISubjectStorageHandler - called by StorageService background thread.
    /// </summary>
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (_client == null)
            return false;

        if (!_subjectPaths.TryGetValue(subject, out var path))
            return false;

        // Mark as own write to prevent feedback loop
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        MarkAsOwnWrite(fullPath);

        var json = _serializer.Serialize(subject);

        // Update content hash
        _contentHashes[path] = ComputeHash(json);

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

        // Mark as own write
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        MarkAsOwnWrite(fullPath);

        var json = _serializer.Serialize(subject);

        // Update hash
        _contentHashes[path] = ComputeHash(json);

        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        var children = new Dictionary<string, IInterceptorSubject>(Children);

        _subjectPaths[subject] = path;
        _pathToSubject[path] = subject;
        PlaceInHierarchy(path, subject, children);

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

        // Mark as own write
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        MarkAsOwnWrite(fullPath);

        await _client.DeleteAsync(path, cancellationToken: ct);

        // Remove from tracking
        if (_pathToSubject.TryRemove(path, out var subject))
        {
            _subjectPaths.TryRemove(subject, out _);
        }
        _contentHashes.TryRemove(path, out _);

        _logger?.LogInformation("Deleted from storage: {Path}", path);
    }

    /// <summary>
    /// Writes a blob to storage (upsert semantics).
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        // Mark as own write
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        MarkAsOwnWrite(fullPath);

        await _client.WriteAsync(path, content, append: false, cancellationToken: ct);
        _logger?.LogDebug("Wrote blob to storage: {Path}", path);
    }

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        // Mark as own write
        var fullPath = Path.GetFullPath(Path.Combine(ConnectionString, path));
        MarkAsOwnWrite(fullPath);

        await _client.DeleteAsync(path, cancellationToken: ct);
        _logger?.LogDebug("Deleted blob from storage: {Path}", path);
    }

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    public async Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

        return await _client.OpenReadAsync(path, cancellationToken: ct);
    }

    private async Task<IInterceptorSubject?> CreateSubjectFromBlobAsync(Blob blob, CancellationToken ct)
    {
        var context = ((IInterceptorSubject)this).Context;
        var extension = Path.GetExtension(blob.FullPath).ToLowerInvariant();

        // JSON files - check if [Configurable] type
        if (extension == ".json")
        {
            var json = await _client!.ReadTextAsync(blob.FullPath, cancellationToken: ct);

            // Try to deserialize as a configurable subject
            try
            {
                var subject = _serializer.Deserialize(json, context);
                if (subject != null)
                {
                    return subject;
                }
            }
            catch
            {
                // Not a configurable subject, fall through to JsonFile
            }

            // Create JsonFile for plain JSON
            return new Files.JsonFile(context, this, blob.FullPath) { Content = json };
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            var subject = CreateFileSubject(mappedType, blob.FullPath, context);
            SetFileMetadata(subject, blob);

            // For MarkdownFile, load content and extract title
            if (subject is Files.MarkdownFile markdownFile)
            {
                await LoadMarkdownTitleAsync(markdownFile, blob, ct);
            }

            return subject;
        }

        // Default to GenericFile
        var genericFile = CreateFileSubject(typeof(Files.GenericFile), blob.FullPath, context);
        SetFileMetadata(genericFile, blob);
        return genericFile;
    }

    private async Task LoadMarkdownTitleAsync(Files.MarkdownFile markdownFile, Blob blob, CancellationToken ct)
    {
        try
        {
            // Read content to extract title from front matter
            var content = await _client!.ReadTextAsync(blob.FullPath, cancellationToken: ct);
            var title = Files.MarkdownFile.ExtractTitleFromContent(content);
            if (title != null)
            {
                markdownFile.SetTitle(title);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load markdown title for: {Path}", blob.FullPath);
        }
    }

    private void SetFileMetadata(IInterceptorSubject? subject, Blob blob)
    {
        if (subject == null)
            return;

        // Set file metadata from blob
        SetPropertyIfExists(subject, "FileSize", blob.Size ?? 0L);
        if (blob.LastModificationTime.HasValue)
        {
            SetPropertyIfExists(subject, "LastModified", blob.LastModificationTime.Value);
        }
    }

    private IInterceptorSubject? CreateFileSubject(Type type, string blobPath, IInterceptorSubjectContext context)
    {
        try
        {
            // Try constructor with (context, storage, path)
            var ctor = type.GetConstructor([typeof(IInterceptorSubjectContext), typeof(FluentStorageContainer), typeof(string)]);
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke([context, this, blobPath]);
            }

            // Try constructor with (storage, path)
            ctor = type.GetConstructor([typeof(FluentStorageContainer), typeof(string)]);
            if (ctor != null)
            {
                return (IInterceptorSubject)ctor.Invoke([this, blobPath]);
            }

            // Try constructor with just (path)
            ctor = type.GetConstructor([typeof(string)]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([blobPath]);
                // Set storage reference if available
                SetPropertyIfExists(subject, "Storage", this);
                return subject;
            }

            // Fall back to constructor with just context
            ctor = type.GetConstructor([typeof(IInterceptorSubjectContext)]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([context]);
                // Set properties if available
                SetPropertyIfExists(subject, "Storage", this);
                SetPropertyIfExists(subject, "BlobPath", blobPath);
                SetPropertyIfExists(subject, "FilePath", blobPath);
                SetPropertyIfExists(subject, "FileName", Path.GetFileName(blobPath));
                return subject;
            }

            // Fall back to parameterless constructor
            ctor = type.GetConstructor([]);
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke([]);
                // Set properties if available
                SetPropertyIfExists(subject, "Storage", this);
                SetPropertyIfExists(subject, "BlobPath", blobPath);
                SetPropertyIfExists(subject, "FilePath", blobPath);
                SetPropertyIfExists(subject, "FileName", Path.GetFileName(blobPath));
                return subject;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create file subject for: {Path}", blobPath);
        }

        return null;
    }

    private static void SetPropertyIfExists(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            try { prop.SetValue(target, value); } catch { }
        }
    }

    private void PlaceInHierarchy(string path, IInterceptorSubject subject, Dictionary<string, IInterceptorSubject> children)
    {
        // Normalize path separators
        path = path.Replace('\\', '/').TrimStart('/');
        var segments = path.Split('/');

        if (segments.Length == 1)
        {
            // Direct child of storage
            children[segments[0]] = subject;
            return;
        }

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!children.TryGetValue(folderName, out var existing))
            {
                var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
                var folder = new VirtualFolder(((IInterceptorSubject)this).Context, this, relativePath);
                children[folderName] = folder;
                children = folder.Children;
            }
            else if (existing is VirtualFolder vf)
            {
                children = vf.Children;
            }
            else
            {
                // Conflict - file exists where we need a folder
                _logger?.LogWarning("Path conflict at {Segment} for {Path}", folderName, path);
                return;
            }
        }

        children[segments[^1]] = subject;
    }

    private void RemoveFromHierarchy(string path, Dictionary<string, IInterceptorSubject> children)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        var segments = path.Split('/');

        if (segments.Length == 1)
        {
            children.Remove(segments[0]);
            return;
        }

        // Navigate to parent folder
        var current = children;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetValue(segments[i], out var folder) || folder is not VirtualFolder vf)
                return;
            current = vf.Children;
        }

        current.Remove(segments[^1]);

        // TODO: Clean up empty VirtualFolders
    }

    public void Dispose()
    {
        _fileEventSubscription?.Dispose();
        _fileEvents.Dispose();
        _watcher?.Dispose();
        _watcher = null;
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
    }
}

/// <summary>
/// Status of a storage connection.
/// </summary>
public enum StorageStatus
{
    Disconnected,
    Initializing,
    Connected,
    Error
}

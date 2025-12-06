using System.Collections.Concurrent;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage root using FluentStorage. Implements ISubjectStorageHandler.
/// </summary>
[InterceptorSubject, Configurable]
public partial class FluentStorageContainer : ISubjectStorageHandler, ITitleProvider, IIconProvider, IDisposable
{
    // MudBlazor Icons.Material.Filled.Storage
    private const string StorageIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M2,20H22V16H2V20M4,17H6V19H4V17M2,4V8H22V4H2M6,7H4V5H6V7M2,14H22V10H2V14M4,11H6V13H4V11Z\" /></svg>";

    private IBlobStorage? _client;
    // Thread-safe: accessed from StorageService background thread and UI thread
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();

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

        // Clear existing children
        _subjectPaths.Clear();

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

    /// <summary>
    /// ISubjectStorageHandler - called by StorageService background thread.
    /// </summary>
    public async Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        if (_client == null)
            return false;

        if (!_subjectPaths.TryGetValue(subject, out var path))
            return false;

        var json = _serializer.Serialize(subject);
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

        var json = _serializer.Serialize(subject);
        await _client.WriteTextAsync(path, json, cancellationToken: ct);

        var children = new Dictionary<string, IInterceptorSubject>(Children);
        
        _subjectPaths[subject] = path;
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

        await _client.DeleteAsync(path, cancellationToken: ct);

        // Remove from tracking
        var subject = _subjectPaths.FirstOrDefault(kvp => kvp.Value == path).Key;
        if (subject != null)
        {
            _subjectPaths.TryRemove(subject, out _);
        }

        _logger?.LogInformation("Deleted from storage: {Path}", path);
    }

    /// <summary>
    /// Writes a blob to storage (upsert semantics).
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Storage not connected");

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
                    // Check if type has [Configurable] attribute
                    var hasConfigurable = subject.GetType().GetCustomAttributes(
                        typeof(ConfigurableAttribute), true).Length > 0;

                    if (hasConfigurable)
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

    public void Dispose()
    {
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

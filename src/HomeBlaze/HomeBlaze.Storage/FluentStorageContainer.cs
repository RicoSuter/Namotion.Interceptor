using System.Text;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
using StoragePath = HomeBlaze.Storage.Internal.StoragePath;

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

    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();

    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly ILogger<FluentStorageContainer>? _logger;

    // The index, the reconciler and the tree below Children are only touched on the worker.
    private StorageIndex _index = new();
    private StorageReconciler? _reconciler;
    private StorageWorker? _worker;

    private StorageFileWatcher? _fileWatcher;
    private string? _storageDirectory;

    private StorageWorker Worker => _worker
        ?? throw new InvalidOperationException("Storage not connected");

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
    /// Initializes the storage client based on configuration and loads the subject tree.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var isInMemory = StorageType == "inmemory";
        if (!isInMemory && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        Status = StorageStatus.Initializing;
        try
        {
            // A reconnect starts from scratch. A watcher or worker left running would keep changing the tree.
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _worker?.Dispose();
            var previousClient = _client;

            _storageDirectory = isInMemory ? null : ResolveStorageDirectory();
            _client = StorageType switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
                "inmemory" => StorageFactory.Blobs.InMemory(),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };
            previousClient?.Dispose();

            _index = new StorageIndex();
            var reconciler = new StorageReconciler(_client, this, _subjectFactory, _serializer, _index, _logger);
            _reconciler = reconciler;
            _worker = new StorageWorker(_logger);

            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                isInMemory ? "(in-memory)" : _storageDirectory);

            // Started before the first pass, so a change during startup leads to another pass.
            if (EnableFileWatching && !isInMemory)
            {
                StartFileWatching();
            }

            await _worker.RunAsync(token => reconciler.ReconcileAsync(NoPaths, allNamed: false, token), cancellationToken);

            Status = StorageStatus.Connected;
            _logger?.LogInformation("Storage loaded: {Count} entries.", _index.Count);
        }
        catch (Exception ex)
        {
            Status = StorageStatus.Error;
            _logger?.LogError(ex, "Failed to connect to storage");
            throw;
        }
    }

    private void StartFileWatching()
    {
        _fileWatcher = new StorageFileWatcher(
            _storageDirectory!,
            ProcessFileEventAsync,
            () => ReconcileAsync(allNamed: true),
            _logger);

        _fileWatcher.Start();
    }

    internal Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        var namedPaths = new HashSet<string>(StringComparer.Ordinal) { GetRelativePath(e.FullPath) };
        if (e is RenamedEventArgs renamed)
        {
            namedPaths.Add(GetRelativePath(renamed.OldFullPath));
        }

        return ReconcileAsync(namedPaths);
    }

    private string GetRelativePath(string fileSystemPath)
        => Path.GetRelativePath(_storageDirectory!, fileSystemPath).Replace('\\', '/');

    /// <summary>
    /// Runs one pass on the worker. A pass that fails is logged and sets <see cref="Status"/> to
    /// <see cref="StorageStatus.Error"/>, and the next pass tries again.
    /// </summary>
    internal Task ReconcileAsync(IReadOnlySet<string>? namedPaths = null, bool allNamed = false)
    {
        var worker = _worker;
        var reconciler = _reconciler;
        if (worker == null || reconciler == null)
        {
            return Task.CompletedTask;
        }

        return worker.RunAsync(async token =>
        {
            try
            {
                await reconciler.ReconcileAsync(namedPaths ?? NoPaths, allNamed, token);
                if (Status != StorageStatus.Connected)
                {
                    Status = StorageStatus.Connected;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Status = StorageStatus.Error;
                _logger?.LogError(exception, "Failed to reconcile the storage, the next pass tries again");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// IConfigurationWriter - called by ConfigurationManager background thread.
    /// </summary>
    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker == null)
            return Task.FromResult(false);

        return worker.RunAsync(async token =>
        {
            if (!_index.TryGetPath(subject, out var path) || !_index.TryGet(path, out var entry))
                return false;

            await WriteAndRecordAsync(entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

            _logger?.LogDebug("Saved subject to storage: {Path}", path);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    /// <exception cref="ArgumentException">The path is hidden or temporary and would not be loaded again.</exception>
    /// <exception cref="InvalidOperationException">A file exists at the path, or its key in the hierarchy is taken.</exception>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var relativePath = StoragePath.Normalize(path);
        if (StoragePathFilter.IsIgnored(relativePath))
            throw new ArgumentException($"A subject at '{path}' would not be loaded again: the path is hidden or temporary.", nameof(path));

        await Worker.RunAsync(async token =>
        {
            if (_index.TryGet(relativePath, out _) || await Client.ExistsAsync(relativePath, token))
                throw new InvalidOperationException($"A file already exists at '{path}'.");

            if (!_reconciler!.IsKeyFree(relativePath, subject))
                throw new InvalidOperationException($"A subject already exists at '{path}'.");

            var entry = new StorageEntry { Path = relativePath, IsFolder = false, Subject = subject };
            await WriteAndRecordAsync(entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

            _index.EnsureFolders(relativePath);
            _index.Set(entry);
            _reconciler.Apply();

            _logger?.LogInformation("Added subject to storage: {Path}", relativePath);
        }, cancellationToken);
    }

    // Recording version and hash is what keeps the event that this write raises from reloading the subject.
    private async Task WriteAndRecordAsync(StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content);
        await Client.WriteAsync(entry.Path, stream, append: false, cancellationToken: cancellationToken);

        entry.Hash = StorageHash.Compute(content);
        entry.Version = await _reconciler!.GetVersionAsync(entry.Path, cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Gets metadata about a blob.
    /// </summary>
    public async Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (_storageDirectory != null)
        {
            // Asked for every markdown file that loads, so the directory is not listed to find one entry.
            var file = new FileInfo(GetFileSystemPath(path));
            return file.Exists ? new BlobMetadata(file.Length, file.LastWriteTimeUtc) : null;
        }

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
    public Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
        => Worker.RunAsync(async token =>
        {
            var relativePath = StoragePath.Normalize(path);
            await Client.WriteAsync(relativePath, content, append: false, cancellationToken: token);
            _logger?.LogDebug("Wrote blob to storage: {Path}", relativePath);

            // A file without a subject is picked up by the pass that its event triggers.
            if (!_index.TryGet(relativePath, out var entry) || entry.Subject == null)
                return;

            // Recorded before the subject is refreshed: the write succeeded whether or not the refresh does, and
            // a hash taken after the subject read the file could be of a later content than the subject has.
            entry.Version = await _reconciler!.GetVersionAsync(relativePath, token);
            entry.Hash = entry.Subject is GenericFile ? null : await _reconciler.ComputeHashAsync(relativePath, token);

            if (entry.Subject is IStorageFile file)
            {
                try
                {
                    await file.OnFileChangedAsync(token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger?.LogWarning(exception, "Failed to refresh the subject after writing: {Path}", relativePath);
                }
            }
        }, cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage and removes from Children.
    /// </summary>
    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
        => Worker.RunAsync(token => DeleteEntryAsync(StoragePath.Normalize(path), token), cancellationToken);

    /// <summary>
    /// Opens the create subject wizard to add a new subject to this storage.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a subject by finding its path in the index.
    /// </summary>
    public Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
        => Worker.RunAsync(token => _index.TryGetPath(subject, out var path)
            ? DeleteEntryAsync(path, token)
            : throw new InvalidOperationException("Subject not found in storage registry"), cancellationToken);

    private async Task DeleteEntryAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (!_index.TryGet(relativePath, out var entry) || entry.IsFolder || entry.Subject == null)
        {
            _logger?.LogWarning("Cannot delete blob - no subject at: {Path}", relativePath);
            return;
        }

        await Client.DeleteAsync(relativePath, cancellationToken: cancellationToken);
        _index.Remove(relativePath);
        _reconciler!.Apply();

        _logger?.LogDebug("Deleted blob from storage: {Path}", relativePath);
    }

    public override void Dispose()
    {
        _fileWatcher?.Dispose();
        _worker?.Dispose();
        _client?.Dispose();
        _client = null;
        Status = StorageStatus.Disconnected;
        base.Dispose();
    }
}

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
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();

    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly ILogger<FluentStorageContainer>? _logger;

    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // Guards the status together with which connection is the current one, so that neither a pass of an ended
    // connection nor a connect that a Dispose overtook writes the status. Never held across an await.
    private readonly Lock _connectionLock = new();
    private int _disposeCount;

    // Replaced as a whole by a reconnect and kept after it ended. An operation reads the field once and uses
    // only what it read, so its item never touches the index or the client of a later connection.
    private volatile StorageConnection? _connection;

    private StorageConnection Connection => _connection is { IsEnded: false } connection
        ? connection
        : throw new InvalidOperationException("Storage not connected");

    /// <summary>The clock for timeouts and timers. Read when the storage connects.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Wraps the storage client when the storage connects.</summary>
    internal Func<IBlobStorage, IBlobStorage>? ClientDecorator { get; set; }

    /// <summary>
    /// The clock of the current connection, for file subjects of this library that limit their own reading of
    /// a stream of the storage.
    /// </summary>
    internal TimeProvider ConnectionTimeProvider => _connection?.TimeProvider ?? TimeProvider;

    /// <inheritdoc cref="TimeLimitedBlobStorage.IsUnresponsive"/>
    internal bool IsStorageUnresponsive => _connection is { IsStorageUnresponsive: true };

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
    /// How often the storage is compared with the subject tree without any file event, in seconds.
    /// Covers changes the file watcher never reports. Default is 300, and 0 switches it off.
    /// </summary>
    [Configuration]
    public partial int ReconcileIntervalSeconds { get; set; }

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
        ReconcileIntervalSeconds = 300;
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
        => GetFileSystemPath(_connection?.StorageDirectory ?? ResolveStorageDirectory(), relativePath);

    private static string GetFileSystemPath(string storageDirectory, string relativePath)
        => Path.GetFullPath(Path.Combine(storageDirectory, relativePath.TrimStart('/', '\\')));

    private string ResolveStorageDirectory()
    {
        var baseDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory
            ?? Directory.GetCurrentDirectory();

        return string.IsNullOrEmpty(ConnectionString)
            ? baseDirectory
            : Path.GetFullPath(ConnectionString, baseDirectory);
    }

    /// <summary>
    /// Initializes the storage client based on configuration and loads the subject tree. A connection that
    /// exists is ended first, and its running work has finished before the new one starts.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var isInMemory = StorageType == "inmemory";
        if (!isInMemory && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        // Read before the wait at the gate, so a Dispose during that wait fails this connect as well.
        int disposeCount;
        lock (_connectionLock)
        {
            disposeCount = _disposeCount;
        }

        // One connect at a time: two of them would each end the same connection and both start a new one.
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            await ReplaceConnectionAsync(isInMemory, disposeCount, cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReplaceConnectionAsync(bool isInMemory, int disposeCount, CancellationToken cancellationToken)
    {
        StorageConnection? previous;
        lock (_connectionLock)
        {
            // After a Dispose during the wait at the gate, the connection that exists is not this connect's to
            // end: a connect that started later can have published it.
            ObjectDisposedException.ThrowIf(_disposeCount != disposeCount, this);
            previous = _connection;
        }

        // Ended before the status is written, so a pass of it that completes now does not overwrite the status.
        previous?.End();

        lock (_connectionLock)
        {
            if (_disposeCount == disposeCount)
            {
                Status = StorageStatus.Initializing;
            }
        }

        StorageConnection? published = null;
        try
        {
            // A reconnect starts from scratch. The item that the previous worker is running still assigns to
            // the tree, so it has to finish before the first pass of the new connection starts.
            if (previous != null)
            {
                await previous.EndAsync(cancellationToken);
            }

            var storageDirectory = isInMemory ? null : ResolveStorageDirectory();
            var client = StorageType switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(storageDirectory!),
                "inmemory" => StorageFactory.Blobs.InMemory(),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };

            client = ClientDecorator?.Invoke(client) ?? client;

            var connection = new StorageConnection(
                client, storageDirectory, this, _subjectFactory, _serializer, TimeProvider, _logger);
            Publish(connection, disposeCount);
            published = connection;

            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                isInMemory ? "(in-memory)" : storageDirectory);

            // Started before the first pass, so a change during startup leads to another pass.
            connection.StartTrigger(
                (namedPaths, allNamed) => ReconcileAsync(connection, namedPaths, allNamed),
                TimeSpan.FromSeconds(ReconcileIntervalSeconds),
                watchFiles: EnableFileWatching && !isInMemory);

            using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
            await connection.Worker.RunAsync(async token =>
            {
                await connection.Reconciler.ReconcileAsync(NoPaths, allNamed: false, token);
                _logger?.LogInformation("Storage loaded: {Count} entries.", connection.Index.Count);
            }, passCancellation.Token);

            SetStatus(connection, StorageStatus.Connected);
        }
        catch (Exception exception)
        {
            if (published != null)
            {
                SetStatus(published, StorageStatus.Error);
            }
            else
            {
                lock (_connectionLock)
                {
                    if (_disposeCount == disposeCount)
                    {
                        Status = StorageStatus.Error;
                    }
                }
            }

            _logger?.LogError(exception, "Failed to connect to storage");
            throw;
        }
    }

    private void Publish(StorageConnection connection, int disposeCount)
    {
        lock (_connectionLock)
        {
            if (_disposeCount == disposeCount)
            {
                _connection = connection;
                return;
            }
        }

        // Dispose ran since this connect started and could not end a connection that did not exist yet.
        connection.Dispose();
        throw new ObjectDisposedException(nameof(FluentStorageContainer));
    }

    private void SetStatus(StorageConnection connection, StorageStatus status)
    {
        lock (_connectionLock)
        {
            if (ReferenceEquals(_connection, connection) && !connection.IsEnded && Status != status)
            {
                Status = status;
            }
        }
    }

    /// <summary>
    /// Runs one pass on the worker. A pass that fails is logged and sets <see cref="Status"/> to
    /// <see cref="StorageStatus.Error"/>, and the next pass tries again. A pass of a connection that has ended
    /// changes nothing.
    /// </summary>
    internal Task ReconcileAsync(IReadOnlySet<string>? namedPaths = null, bool allNamed = false)
        => _connection is { } connection ? ReconcileAsync(connection, namedPaths ?? NoPaths, allNamed) : Task.CompletedTask;

    private async Task ReconcileAsync(StorageConnection connection, IReadOnlySet<string> namedPaths, bool allNamed)
    {
        try
        {
            await connection.Worker.RunAsync(async token =>
            {
                try
                {
                    await connection.Reconciler.ReconcileAsync(namedPaths, allNamed, token);
                    SetStatus(connection, StorageStatus.Connected);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    SetStatus(connection, StorageStatus.Error);
                    _logger?.LogError(exception, "Failed to reconcile the storage, the next pass tries again");
                }
            }, connection.Token);
        }
        catch (Exception exception) when (connection.IsEnded)
        {
            _logger?.LogDebug(exception, "A pass ended with its connection");
        }
    }

    /// <summary>
    /// IConfigurationWriter - called by ConfigurationManager background thread.
    /// </summary>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null or { IsEnded: true })
            return false;

        try
        {
            return await connection.Worker.RunAsync(async token =>
            {
                if (!connection.Index.TryGetPath(subject, out var path) || !connection.Index.TryGet(path, out var entry))
                    return false;

                await WriteAndRecordAsync(connection, entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

                _logger?.LogDebug("Saved subject to storage: {Path}", path);
                return true;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (connection.IsEnded && !cancellationToken.IsCancellationRequested)
        {
            // The connection ended before the item ran, which is the same as no connection.
            return false;
        }
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

        var connection = Connection;
        await connection.Worker.RunAsync(async token =>
        {
            if (connection.Index.TryGet(relativePath, out _) || await connection.Client.ExistsAsync(relativePath, token))
                throw new InvalidOperationException($"A file already exists at '{path}'.");

            if (!connection.Reconciler.IsKeyFree(relativePath, subject))
                throw new InvalidOperationException($"A subject already exists at '{path}'.");

            var entry = new StorageEntry { Path = relativePath, IsFolder = false, Subject = subject };
            await WriteAndRecordAsync(connection, entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

            connection.Index.EnsureFolders(relativePath);
            connection.Index.Set(entry);
            connection.Reconciler.Apply();

            _logger?.LogInformation("Added subject to storage: {Path}", relativePath);
        }, cancellationToken);
    }

    // Recording version and hash is what keeps the event that this write raises from reloading the subject.
    private static async Task WriteAndRecordAsync(
        StorageConnection connection, StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        // Not disposed when the write fails: a write that was given up reads from it once the storage goes on,
        // and would otherwise leave an empty file.
        var stream = new MemoryStream(content);
        await connection.Client.WriteAsync(entry.Path, stream, append: false, cancellationToken: cancellationToken);
        await stream.DisposeAsync();

        entry.Hash = StorageHash.Compute(content);
        entry.Version = await connection.Reconciler.GetVersionAsync(entry.Path, cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Gets metadata about a blob.
    /// </summary>
    /// <remarks>
    /// On disk, asking for a path in a folder that does not exist creates that folder, as reading the path does.
    /// </remarks>
    public async Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var relativePath = StoragePath.Normalize(path);
        if (relativePath.Length == 0)
            return null;

        // Through the client also for a storage on disk: subjects ask while they load on the worker, and only
        // a call on the client is limited.
        var blobs = await Connection.Client.GetBlobsAsync([relativePath], cancellationToken);
        return blobs.FirstOrDefault() is { } blob
            ? new BlobMetadata(blob.Size ?? 0, blob.LastModificationTime?.UtcDateTime)
            : null;
    }

    /// <summary>
    /// IStorageContainer - Reads a blob from storage.
    /// </summary>
    public async Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken)
    {
        return await Connection.Client.OpenReadAsync(path, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Writes a blob to storage.
    /// </summary>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await connection.Worker.RunAsync(async token =>
        {
            var relativePath = StoragePath.Normalize(path);
            await connection.Client.WriteAsync(relativePath, content, append: false, cancellationToken: token);
            _logger?.LogDebug("Wrote blob to storage: {Path}", relativePath);

            // A file without a subject is picked up by the pass that its event triggers.
            if (!connection.Index.TryGet(relativePath, out var entry) || entry.Subject == null)
                return;

            // Recorded before the subject is refreshed: a hash taken after the subject read the file could be
            // of a later content than the subject has.
            entry.Version = await connection.Reconciler.GetVersionAsync(relativePath, token);
            entry.Hash = entry.Subject is GenericFile ? null : await connection.Reconciler.ComputeHashAsync(relativePath, token);

            if (entry.Subject is IStorageFile file)
            {
                try
                {
                    await file.OnFileChangedAsync(token);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    // The bytes are written, so the caller gets no failure. Without a hash, the pass that the
                    // event of this write names loads the subject again. A plain file never has a hash and is
                    // refreshed only when its version differs.
                    entry.Hash = null;
                    if (entry.Subject is GenericFile)
                    {
                        entry.Version = default;
                    }

                    _logger?.LogWarning(exception, "Failed to refresh the subject after writing: {Path}", relativePath);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage and removes from Children.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await connection.Worker.RunAsync(
            token => DeleteEntryAsync(connection, StoragePath.Normalize(path), token), cancellationToken);
    }

    /// <summary>
    /// Opens the create subject wizard to add a new subject to this storage.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a subject by finding its path in the index.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is not the subject of a file in this storage.</exception>
    public async Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await connection.Worker.RunAsync(token =>
        {
            // A folder has an entry too, but no file that could be deleted.
            if (!connection.Index.TryGetPath(subject, out var path) ||
                !connection.Index.TryGet(path, out var entry) ||
                entry.IsFolder)
            {
                throw new InvalidOperationException("Subject not found in storage registry");
            }

            return DeleteEntryAsync(connection, path, token);
        }, cancellationToken);
    }

    private async Task DeleteEntryAsync(StorageConnection connection, string relativePath, CancellationToken cancellationToken)
    {
        if (!connection.Index.TryGet(relativePath, out var entry) || entry.IsFolder || entry.Subject == null)
        {
            _logger?.LogWarning("Cannot delete blob - no subject at: {Path}", relativePath);
            return;
        }

        await connection.Client.DeleteAsync(relativePath, cancellationToken: cancellationToken);
        connection.Index.Remove(relativePath);
        connection.Reconciler.Apply();

        _logger?.LogDebug("Deleted blob from storage: {Path}", relativePath);
    }

    public override void Dispose()
    {
        // The connection is ended outside the lock, because cancelling it runs code of the work it cancels.
        // A connect can publish another one meanwhile, hence the loop.
        while (true)
        {
            var connection = _connection;
            connection?.Dispose();

            lock (_connectionLock)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _disposeCount++;
                    Status = StorageStatus.Disconnected;
                    break;
                }
            }
        }

        base.Dispose();
    }
}

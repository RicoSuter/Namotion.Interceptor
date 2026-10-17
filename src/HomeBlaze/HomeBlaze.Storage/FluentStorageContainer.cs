using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
using StoragePath = HomeBlaze.Storage.Internal.StoragePath;

namespace HomeBlaze.Storage;

/// <summary>
/// The root of a storage on a FluentStorage backend. Its subject tree follows the storage through reconcile
/// passes, and every change of the storage or the tree runs on one worker per connection.
/// </summary>
/// <remarks>
/// Every call on the storage is limited to 30 seconds. An operation whose call hits the limit fails with a
/// <see cref="TimeoutException"/>, and so does every operation that is called before that call has returned.
/// A write that failed this way can still reach the storage later.
/// </remarks>
[Category("System")]
[Description("Mounts a storage location and loads the files in it as subjects")]
[DisplayName("Storage")]
[SubjectIcon("Storage")]
[InterceptorSubject]
public partial class FluentStorageContainer :
    BackgroundService,
    IStorageContainer, IConfigurationWriter, ITitleProvider, IIconProvider, IConfigurable, IMetadataContainer
{
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();

    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly ILogger<FluentStorageContainer>? _logger;

    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // Guards the status together with which connection is the current one, so that neither a pass of an ended
    // connection nor a connect that a Dispose overtook writes the status. Never held across an await.
    private readonly Lock _connectionLock = new();
    private bool _isDisposed;

    // True from StopAsync until the next StartAsync. Read in the same step that publishes a connection, so a
    // stop either keeps that connection from following its storage or finds it and stops it.
    private bool _isStopped;

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

    /// <inheritdoc cref="TimeLimitedBlobStorage.IsUnresponsive"/>
    internal bool IsStorageUnresponsive => _connection is { IsStorageUnresponsive: true };

    /// <summary>Gets whether the caller runs within work of the worker of the current connection.</summary>
    internal bool IsCallFromWithinWorkerItem => _connection?.Worker.IsCallFromWithinItem == true;

    /// <inheritdoc cref="StorageConnection.SimulateFileWatcherError"/>
    internal void SimulateFileWatcherError(Exception exception) => _connection?.SimulateFileWatcherError(exception);

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
    /// Covers changes the file watcher never reports. Default is 300. Zero or a negative value switches it off.
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

    /// <inheritdoc />
    public partial ImmutableArray<string> ChildOrder { get; private set; }

    /// <inheritdoc />
    public bool HasOwnMetadata => false;

    /// <inheritdoc />
    public FolderMetadata? Metadata => null;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ChildNames => [.. Children.Keys];

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
        ChildOrder = [];
    }
        
    /// <summary>
    /// Connects to the storage and follows it from then on.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_connectionLock)
        {
            _isStopped = false;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => ConnectAsync(stoppingToken);

    /// <summary>
    /// Stops following the storage: neither a file event nor the periodic timer starts a pass any more, so
    /// the subject tree stays as it is. The connection stays open, so subjects can still be read and saved.
    /// <see cref="StartAsync"/> connects again and follows the storage as before.
    /// </summary>
    /// <remarks>
    /// A pass that is running or already queued on the worker still completes. What such a pass or an earlier
    /// one created and did not place is released after it, because no pass places it any more. An operation
    /// after the stop does not place it either.
    /// </remarks>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StorageConnection? connection;
        lock (_connectionLock)
        {
            _isStopped = true;
            connection = _connection;
        }

        if (connection != null)
        {
            connection.StopTrigger();
            _ = ReleaseUnplacedSubjectsAsync(connection);
        }

        return base.StopAsync(cancellationToken);
    }

    // Not waited for by the stop: the worker can be held by a pass, or by a storage that does not answer.
    private async Task ReleaseUnplacedSubjectsAsync(StorageConnection connection)
    {
        try
        {
            await connection.Worker.RunAsync(
                _ =>
                {
                    connection.Reconciler.ReleaseUnplacedSubjects();
                    return Task.CompletedTask;
                },
                connection.Token);
        }
        catch (Exception exception)
        {
            // The end of a connection releases them as well.
            _logger?.LogDebug(exception, "The subjects that are not placed were not released when the storage was stopped");
        }
    }

    /// <summary>
    /// IConfigurable implementation - called after configuration properties are updated. Connects again when a
    /// setting of the connection has changed or <see cref="Status"/> is not <see cref="StorageStatus.Connected"/>,
    /// which rebuilds every subject. The status is also not connected after a pass that failed.
    /// </summary>
    /// <remarks>A container that was stopped does not follow its storage after this either, see <see cref="StopAsync"/>.</remarks>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
        => IsConnectedWith(Settings) ? Task.CompletedTask : ConnectAsync(isOnlyForChangedSettings: true, cancellationToken);

    private bool IsConnectedWith(StorageConnectionSettings settings)
        => Status == StorageStatus.Connected && _connection is { IsEnded: false } connection && connection.Settings == settings;

    private StorageConnectionSettings Settings
        => new(StorageType, ConnectionString, ContainerName, EnableFileWatching, ReconcileIntervalSeconds);

    private string ResolveStorageDirectory(string connectionString)
    {
        var baseDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory
            ?? Directory.GetCurrentDirectory();

        return Path.GetFullPath(connectionString, baseDirectory);
    }

    /// <summary>
    /// Initializes the storage client based on configuration and loads the subject tree. A connection that
    /// exists is ended first, and its running work has finished before the new one starts.
    /// </summary>
    /// <remarks>A container that was stopped does not follow its storage after this either, see <see cref="StopAsync"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The container is disposed.</exception>
    public Task ConnectAsync(CancellationToken cancellationToken)
        => ConnectAsync(isOnlyForChangedSettings: false, cancellationToken);

    private async Task ConnectAsync(bool isOnlyForChangedSettings, CancellationToken cancellationToken)
    {
        // Read once, so the connection is created with exactly the settings it is later compared by.
        var settings = Settings;
        if (!settings.IsInMemory && string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        // One connect at a time: two of them would each end the same connection and both start a new one.
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            // Decided here and not before the wait: the connect that this one waited for has done the work.
            if (!isOnlyForChangedSettings || !IsConnectedWith(settings))
            {
                await ReplaceConnectionAsync(settings, cancellationToken);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReplaceConnectionAsync(StorageConnectionSettings settings, CancellationToken cancellationToken)
    {
        StorageConnection? previous;
        lock (_connectionLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            previous = _connection;
        }

        // Ended before the status is written, so a pass of it that completes now does not overwrite the status.
        previous?.End();

        lock (_connectionLock)
        {
            if (!_isDisposed)
            {
                Status = StorageStatus.Initializing;
            }
        }

        StorageConnection? connection = null;
        try
        {
            // A reconnect starts from scratch. The item that the previous worker is running still assigns to
            // the tree, so it has to finish before the first pass of the new connection starts.
            if (previous != null)
            {
                await previous.EndAsync(cancellationToken);
            }

            var storageDirectory = settings.IsInMemory ? null : ResolveStorageDirectory(settings.ConnectionString);
            var client = settings.StorageType switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(storageDirectory!),
                "inmemory" => StorageFactory.Blobs.InMemory(),
                _ => throw new NotSupportedException($"Storage type '{settings.StorageType}' is not supported")
            };

            client = ClientDecorator?.Invoke(client) ?? client;

            connection = new StorageConnection(
                settings, client, storageDirectory, this, _subjectFactory, _serializer, TimeProvider, _logger);

            // Queued before the connection can be found, so that it is the first item of the worker: an
            // operation that ran before it would assign a tree that holds nothing of the storage yet. It
            // lists only once the connection follows its storage, so a change during startup leads to
            // another pass.
            var isPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
            var firstPass = RunFirstPassAsync(connection, isPublished.Task, passCancellation.Token);
            try
            {
                Publish(connection);

                // Before the first listing, so that a change during startup raises an event. Not within
                // Publish: a watcher on a storage that does not answer can stall while it starts, and would
                // hold the lock that a stop, a dispose and every change of the status wait for.
                connection.TryStartFileWatcher();
                isPublished.SetResult(true);
            }
            catch
            {
                isPublished.SetResult(false);
                throw;
            }

            _logger?.LogInformation("Connected to storage: {Type} at {Path}", settings.StorageType,
                settings.IsInMemory ? "(in-memory)" : storageDirectory);

            await firstPass;
        }
        catch (Exception exception)
        {
            var isCancelledByCaller = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var wasStillInitializing = false;
            lock (_connectionLock)
            {
                // Only where the first pass has not written the status itself. Written here after it, the
                // status could replace that of a pass that ran in between.
                if (!_isDisposed && Status == StorageStatus.Initializing)
                {
                    Status = isCancelledByCaller ? StorageStatus.Disconnected : StorageStatus.Error;
                    wasStillInitializing = true;
                }
            }

            // The worker does not run an item whose caller has cancelled, so a first pass that was cancelled
            // before it started has not asked for its retry, as one that is cancelled while it runs does.
            if (isCancelledByCaller && wasStillInitializing)
            {
                connection?.NotifyPassFailed();
            }

            if (isCancelledByCaller)
            {
                _logger?.LogInformation("Connecting to the storage was cancelled");
            }
            else
            {
                _logger?.LogError(exception, "Failed to connect to storage");
            }

            throw;
        }
    }

    private Task RunFirstPassAsync(StorageConnection connection, Task<bool> isPublished, CancellationToken cancellationToken)
        => connection.Worker.RunAsync(async token =>
        {
            if (!await isPublished)
            {
                return;
            }

            try
            {
                await connection.Reconciler.ReconcileAsync(NoPaths, allNamed: false, token);
            }
            catch (Exception exception)
            {
                // Cancelled by the caller of the connect, or with the connection, whose status nobody reads.
                SetStatus(connection, token.IsCancellationRequested ? StorageStatus.Disconnected : StorageStatus.Error);

                // Also after a cancellation by the caller: unless the storage was stopped, the connection goes
                // on following it, and only another pass replaces that status.
                if (connection.GetOutcomeOfFailedPass(exception) == ReconcileOutcome.Failed)
                {
                    connection.NotifyPassFailed();
                }

                throw;
            }

            SetStatus(connection, StorageStatus.Connected);
            _logger?.LogInformation("Storage loaded: {Count} entries.", connection.Index.Entries.Count);
        }, cancellationToken);

    /// <summary>
    /// Makes the connection the current one and lets it follow its storage, unless the container is stopped.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The container is disposed. The connection is then disposed as well.</exception>
    private void Publish(StorageConnection connection)
    {
        lock (_connectionLock)
        {
            if (!_isDisposed)
            {
                _connection = connection;

                if (!_isStopped)
                {
                    connection.StartTrigger((namedPaths, allNamed) => ReconcileAsync(connection, namedPaths, allNamed));
                }

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
            if (!_isDisposed && ReferenceEquals(_connection, connection) && !connection.IsEnded && Status != status)
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

    /// <returns>How the pass ended. A pass that ended with its connection counts as finished.</returns>
    private async Task<ReconcileOutcome> ReconcileAsync(StorageConnection connection, IReadOnlySet<string> namedPaths, bool allNamed)
    {
        // Not on the worker, which a watcher that stalls while it starts would hold. Changes since the watcher
        // stopped have raised no event, so every file counts as named.
        allNamed |= connection.TryStartFileWatcher();

        var outcome = ReconcileOutcome.Finished;
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
                    _logger?.LogError(exception, "Failed to reconcile the storage, another pass tries again");
                    outcome = connection.GetOutcomeOfFailedPass(exception);
                }
            }, connection.Token);
        }
        catch (Exception exception) when (connection.IsEnded)
        {
            _logger?.LogDebug(exception, "A pass ended with its connection");
        }

        return outcome;
    }

    /// <summary>
    /// Takes the order from the <c>.metadata.json</c> in the root of the storage, or none without one. The root has no
    /// metadata of its own, so the other keys are ignored. Called on the worker.
    /// </summary>
    internal void ApplyMetadata(StorageMetadataDocument? document)
    {
        var childOrder = document?.ToChildOrder() ?? [];
        if (!childOrder.SequenceEqual(ChildOrder))
        {
            ChildOrder = childOrder;
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the root of a storage has no metadata of its own.</exception>
    public Task WriteMetadataAsync(FolderMetadata? metadata, CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("The root of a storage has no metadata of its own."));

    /// <inheritdoc />
    public Task WriteChildMetadataAsync(IInterceptorSubject child, SubjectMetadata? metadata, CancellationToken cancellationToken)
        => WriteSubjectMetadataAsync(child, metadata, cancellationToken);

    /// <inheritdoc />
    public Task MoveChildAsync(string name, string targetName, CancellationToken cancellationToken)
        => MoveChildAsync(string.Empty, name, targetName, cancellationToken);

    /// <summary>
    /// IConfigurationWriter - Saves the file of the subject, or of the subject it is nested in through
    /// [Configuration] properties.
    /// </summary>
    /// <returns>False when no file in this storage contains the subject, or the storage is not connected.</returns>
    public async Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null or { IsEnded: true })
            return false;

        var isWritten = false;
        try
        {
            await connection.Worker.RunAsync(async token =>
            {
                // Only the subject of a file has an entry. A subject nested in its configuration is written with it.
                StorageEntry? entry = null;
                var fileSubject = subject.TryGetConfigurationOwner(candidate => connection.Index.TryGetEntry(candidate, out entry));
                if (fileSubject is null || entry is null)
                    return;

                await WriteAndRecordAsync(connection, entry, Encoding.UTF8.GetBytes(_serializer.Serialize(fileSubject)), token);
                isWritten = true;

                _logger?.LogDebug("Saved subject to storage: {Path}", entry.Path);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (connection.IsEnded && !cancellationToken.IsCancellationRequested)
        {
            // The connection ended before the item ran, which is the same as no connection.
        }

        return isWritten;
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    /// <exception cref="ArgumentException">The path is hidden, temporary or the name of a metadata file, and would not be loaded again.</exception>
    /// <exception cref="InvalidOperationException">A file exists at the path, or its key in the hierarchy is taken.</exception>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var relativePath = StoragePath.Normalize(path);
        if (StoragePathFilter.IsIgnored(relativePath))
            throw new ArgumentException($"A subject at '{path}' would not be loaded again: the path is hidden or temporary.", nameof(path));

        if (StorageMetadataPath.IsMetadataFile(relativePath))
            throw new ArgumentException($"A subject at '{path}' would be read as a metadata file: names that end with '.metadata' are reserved.", nameof(path));

        var connection = Connection;
        await RunOperationAsync(connection, async token =>
        {
            if (connection.Index.TryGet(relativePath, out _))
                throw new InvalidOperationException($"A file already exists at '{path}'.");

            // Before the storage is asked: on disk, asking about a path creates the folder of that path.
            if (!connection.Reconciler.IsKeyFree(relativePath, subject))
                throw new InvalidOperationException($"A subject already exists at '{path}'.");

            if (await connection.Client.ExistsAsync(relativePath, token))
                throw new InvalidOperationException($"A file already exists at '{path}'.");

            var entry = new StorageEntry { Path = relativePath, IsFolder = false, Subject = subject };
            await WriteAndRecordAsync(connection, entry, Encoding.UTF8.GetBytes(_serializer.Serialize(subject)), token);

            connection.Index.EnsureFolders(relativePath);
            connection.Index.Set(entry);
            connection.Reconciler.Apply();

            _logger?.LogInformation("Added subject to storage: {Path}", relativePath);
        }, cancellationToken);
    }

    /// <summary>
    /// Writes the metadata of a folder to its <c>.metadata.json</c>, keeping the order and the keys it does not know.
    /// </summary>
    /// <param name="folder">The path of the folder within the storage, not empty.</param>
    /// <param name="metadata">The metadata, null to remove it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The folder is not in the tree, or its metadata file could not be read.</exception>
    internal async Task WriteFolderMetadataAsync(string folder, FolderMetadata? metadata, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            // Checked, because writing into a folder that is gone would create it again.
            _ = GetPlacedFolder(connection, folder);

            var path = StorageMetadataPath.GetFolderFilePath(folder);
            var document = GetMetadataDocument(connection, path);
            document.Title = GetValue(metadata?.Title);
            document.NavigationTitle = GetValue(metadata?.NavigationTitle);
            document.Icon = GetValue(metadata?.Icon);
            return WriteMetadataDocumentAsync(connection, path, document, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Writes the metadata of the subject of a JSON file to the metadata file beside it, keeping the keys it does not know.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The subject is not the subject of a JSON file in this storage, or its metadata file could not be read.
    /// </exception>
    internal async Task WriteSubjectMetadataAsync(IInterceptorSubject subject, SubjectMetadata? metadata, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            if (!TryGetJsonEntry(connection, subject, out var entry))
            {
                throw new InvalidOperationException("Only the subject of a JSON file in this storage has metadata.");
            }

            return WriteSubjectMetadataAsync(connection, entry, metadata, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Writes the metadata of a child of a folder that is stored as a JSON file, given by its name.
    /// </summary>
    /// <param name="folder">The path of the folder within the storage, empty for the root.</param>
    /// <param name="name">The name of the child.</param>
    /// <param name="metadata">The metadata, null to remove it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The name is not the name of a child stored as a JSON file.</exception>
    /// <exception cref="InvalidOperationException">The folder is not in the tree, or the metadata file could not be read.</exception>
    internal async Task WriteChildMetadataAsync(string folder, string name, SubjectMetadata? metadata, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            var children = GetChildren(connection, folder, out _);
            if (!children.TryGetValue(name, out var child) || !TryGetJsonEntry(connection, child, out var entry))
            {
                var namesWithMetadata = children
                    .Where(pair => TryGetJsonEntry(connection, pair.Value, out _))
                    .Select(static pair => pair.Key)
                    .Order(StringComparer.Ordinal);
                throw new ArgumentException(
                    $"'{name}' is not a child of the folder stored as a JSON file. Children with metadata: {FormatNames(namesWithMetadata)}.",
                    nameof(name));
            }

            return WriteSubjectMetadataAsync(connection, entry, metadata, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the metadata of a folder and the metadata of its children that are stored as JSON files.
    /// </summary>
    /// <param name="folder">The path of the folder within the storage, empty for the root.</param>
    /// <param name="cancellationToken">Cancels waiting for the worker.</param>
    /// <exception cref="InvalidOperationException">The folder is not in the tree.</exception>
    internal async Task<ContainerMetadata> GetContainerMetadataAsync(string folder, CancellationToken cancellationToken)
    {
        var connection = Connection;
        ContainerMetadata? result = null;
        await RunOperationAsync(connection, token =>
        {
            var children = GetChildren(connection, folder, out var virtualFolder);
            var childrenMetadata = new Dictionary<string, SubjectMetadata?>(StringComparer.Ordinal);
            foreach (var (name, child) in children)
            {
                if (TryGetJsonEntry(connection, child, out _))
                {
                    childrenMetadata[name] = child.TryGetSubjectMetadata();
                }
            }

            result = new ContainerMetadata(virtualFolder?.Metadata, childrenMetadata);
            return Task.CompletedTask;
        }, cancellationToken);

        return result!;
    }

    private Task WriteSubjectMetadataAsync(
        StorageConnection connection, StorageEntry entry, SubjectMetadata? metadata, CancellationToken cancellationToken)
    {
        var path = StorageMetadataPath.GetSubjectFilePath(entry.Path);
        var document = GetMetadataDocument(connection, path);
        document.NavigationTitle = GetValue(metadata?.NavigationTitle);
        document.Icon = GetValue(metadata?.Icon);
        document.Location = metadata?.Location;
        document.Alignment = metadata?.Alignment;
        return WriteMetadataDocumentAsync(connection, path, document, cancellationToken);
    }

    private static bool TryGetJsonEntry(
        StorageConnection connection, IInterceptorSubject subject, [NotNullWhen(true)] out StorageEntry? entry)
        => connection.Index.TryGetEntry(subject, out entry) && !entry.IsFolder &&
            subject is IConfigurable && StoragePath.IsJson(entry.Path);

    /// <summary>
    /// Moves a child of a folder to the place of another child in the order in which they are shown, shifting the
    /// children in between, and writes the whole resulting order, which drops the names of children that are gone.
    /// </summary>
    /// <remarks>Moving a child to its own place writes the current order unchanged.</remarks>
    /// <param name="folder">The path of the folder within the storage, empty for the root.</param>
    /// <param name="name">The name of the child to move.</param>
    /// <param name="targetName">The name of the child whose place it takes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The folder is not in the tree, a name is not a child of it, or its metadata file could not be read.</exception>
    internal async Task MoveChildAsync(string folder, string name, string targetName, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            // Read on the worker, so that a move starts from the order the move before it wrote.
            var children = GetChildren(connection, folder, out var virtualFolder);
            var names = GetShownNames(children, virtualFolder?.ChildOrder ?? ChildOrder);

            var index = names.IndexOf(name);
            var targetIndex = names.IndexOf(targetName);
            if (index < 0 || targetIndex < 0)
            {
                throw new InvalidOperationException($"'{name}' and '{targetName}' are not both children of the folder.");
            }

            names.RemoveAt(index);
            names.Insert(targetIndex, name);

            var path = StorageMetadataPath.GetFolderFilePath(folder);
            var document = GetMetadataDocument(connection, path);
            document.Order = [.. names];
            return WriteMetadataDocumentAsync(connection, path, document, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the names of the children of a folder in the order in which they are shown.
    /// </summary>
    /// <param name="folder">The path of the folder within the storage, empty for the root.</param>
    /// <param name="cancellationToken">Cancels waiting for the worker.</param>
    /// <exception cref="InvalidOperationException">The folder is not in the tree.</exception>
    internal async Task<string[]> GetChildOrderAsync(string folder, CancellationToken cancellationToken)
    {
        var connection = Connection;
        string[] names = [];
        await RunOperationAsync(connection, _ =>
        {
            // Read on the worker, so that the order is the one that the write before it stored.
            var children = GetChildren(connection, folder, out var virtualFolder);
            names = [.. GetShownNames(children, virtualFolder?.ChildOrder ?? ChildOrder)];
            return Task.CompletedTask;
        }, cancellationToken);

        return names;
    }

    /// <summary>
    /// Writes the order of the children of a folder: the given names first, in the given order, then the other children
    /// in the order in which they are shown, which drops the names of children that are gone.
    /// </summary>
    /// <param name="folder">The path of the folder within the storage, empty for the root.</param>
    /// <param name="names">The names of the children to show first.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A name is given more than once, or is not a child of the folder.</exception>
    /// <exception cref="InvalidOperationException">The folder is not in the tree, or its metadata file could not be read.</exception>
    internal async Task SetChildOrderAsync(string folder, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);

        // A copy, as the worker reads the names later.
        var requestedNames = names.ToArray();
        var listedNames = new HashSet<string>(StringComparer.Ordinal);
        var repeatedNames = new List<string>();
        foreach (var name in requestedNames)
        {
            // Null arrives from an agent that sends a JSON null in the array.
            if (name is null)
            {
                throw new ArgumentException("A name is null.", nameof(names));
            }

            if (!listedNames.Add(name) && !repeatedNames.Contains(name))
            {
                repeatedNames.Add(name);
            }
        }

        if (repeatedNames.Count > 0)
        {
            throw new ArgumentException($"Names given more than once: {FormatNames(repeatedNames)}.", nameof(names));
        }

        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            var children = GetChildren(connection, folder, out var virtualFolder);
            var unknownNames = requestedNames.Where(name => !children.ContainsKey(name)).ToList();
            if (unknownNames.Count > 0)
            {
                throw new ArgumentException(
                    $"Names that are not children of the folder: {FormatNames(unknownNames)}. Its children: {FormatNames(children.Keys.Order(StringComparer.Ordinal))}.",
                    nameof(names));
            }

            var shownNames = GetShownNames(children, virtualFolder?.ChildOrder ?? ChildOrder);
            var order = new string[shownNames.Count];
            var position = 0;
            foreach (var name in requestedNames)
            {
                order[position++] = name;
            }

            foreach (var name in shownNames)
            {
                if (!listedNames.Contains(name))
                {
                    order[position++] = name;
                }
            }

            var path = StorageMetadataPath.GetFolderFilePath(folder);
            var document = GetMetadataDocument(connection, path);
            document.Order = order;
            return WriteMetadataDocumentAsync(connection, path, document, token);
        }, cancellationToken);
    }

    // Runs on the worker, which replaces the children of a folder.
    private IReadOnlyDictionary<string, IInterceptorSubject> GetChildren(
        StorageConnection connection, string folder, out VirtualFolder? virtualFolder)
    {
        virtualFolder = folder.Length == 0 ? null : GetPlacedFolder(connection, folder);
        return virtualFolder?.Children ?? Children;
    }

    private static List<string> GetShownNames(IReadOnlyDictionary<string, IInterceptorSubject> children, ImmutableArray<string> childOrder)
        => ChildOrdering
            .Order(children, childOrder, static child => child.Key, static child => child.Value.GetNavigationTitle(child.Key))
            .Select(static child => child.Key)
            .ToList();

    private static string FormatNames(IEnumerable<string> names)
    {
        var text = string.Join(", ", names.Select(static name => $"'{name}'"));
        return text.Length > 0 ? text : "none";
    }

    private static VirtualFolder GetPlacedFolder(StorageConnection connection, string folder)
        => connection.Index.TryGet(folder, out var entry) && entry is { IsFolder: true, IsPlaced: true, Subject: VirtualFolder virtualFolder }
            ? virtualFolder
            : throw new InvalidOperationException($"No folder at '{folder}'.");

    // A copy, so that the entry keeps what the file holds until the write has succeeded. A file that could not be read
    // is refused, because writing it would replace what someone wrote by hand.
    private static StorageMetadataDocument GetMetadataDocument(StorageConnection connection, string path)
    {
        if (!connection.Index.TryGet(path, out var entry) || entry.IsFolder)
        {
            return new StorageMetadataDocument();
        }

        return entry.Metadata?.Clone()
            ?? throw new InvalidOperationException($"The metadata file '{path}' could not be read. Fix or delete it, then try again.");
    }

    // Blank means not set, so that a value of only spaces is never written and a save with only such values deletes the file.
    private static string? GetValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    // Runs on the worker. A file that would be empty is deleted, so that a metadata file exists only while it holds something.
    private async Task WriteMetadataDocumentAsync(
        StorageConnection connection, string path, StorageMetadataDocument document, CancellationToken cancellationToken)
    {
        if (document.IsEmpty)
        {
            await DeleteMetadataFileAsync(connection, path, cancellationToken);
            connection.Reconciler.Apply();
            _logger?.LogDebug("Removed empty metadata from storage: {Path}", path);
        }
        else
        {
            var entry = new StorageEntry { Path = path, IsFolder = false, Metadata = document };
            await WriteAndRecordAsync(connection, entry, document.Serialize(), cancellationToken);
            connection.Index.Set(entry);
            connection.Reconciler.Apply();
            _logger?.LogDebug("Wrote metadata to storage: {Path}", path);
        }
    }

    private static async Task DeleteMetadataFileAsync(StorageConnection connection, string path, CancellationToken cancellationToken)
    {
        if (connection.Index.TryGet(path, out var entry))
        {
            // A folder with the name of a metadata file is no metadata file, and deleting the path would delete it with all it holds.
            if (entry.IsFolder)
            {
                return;
            }
        }
        else if (!await connection.Client.ExistsAsync(path, cancellationToken))
        {
            // A file that no pass has listed yet is deleted as well.
            return;
        }

        try
        {
            await connection.Client.DeleteAsync(path, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (CanHaveReachedStorage(exception))
        {
            connection.NotifyChanged(path);
            throw;
        }

        connection.Index.Remove(path);
    }

    // Recording version and hash is what keeps the event that this write raises from reloading the subject.
    private async Task WriteAndRecordAsync(
        StorageConnection connection, StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        await WriteAsync(connection, entry.Path, content, cancellationToken);

        entry.Hash = StorageHash.Compute(content);
        try
        {
            entry.Version = await connection.Reconciler.GetVersionAsync(entry.Path, cancellationToken);
        }
        catch (Exception exception)
        {
            // The file is written, so this is no failure of the write. Without a version the next pass
            // compares the file by its hash and finds what was written.
            entry.Version = default;
            _logger?.LogDebug(exception, "Failed to read the version of the written file: {Path}", entry.Path);
        }
    }

    /// <summary>
    /// Runs an operation on the worker of the connection it was started for.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The connection ended before the operation was done, which is the same as no connection.
    /// </exception>
    private static async Task RunOperationAsync(
        StorageConnection connection, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Worker.RunAsync(operation, cancellationToken);
        }
        catch (OperationCanceledException) when (connection.IsEnded && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Storage not connected");
        }
    }

    private static async Task WriteAsync(
        StorageConnection connection, string path, byte[] content, CancellationToken cancellationToken)
    {
        // Not disposed when the write fails, see TimeLimitedBlobStorage.
        var stream = new MemoryStream(content);
        try
        {
            await connection.Client.WriteAsync(path, stream, append: false, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (CanHaveReachedStorage(exception))
        {
            connection.NotifyChanged(path);
            throw;
        }

        await stream.DisposeAsync();
    }

    // A call that failed can have changed the storage, and one that was given up can still do so. The pass
    // that finds out is asked for by the operation, because not every storage has a file watcher that would.
    private static bool CanHaveReachedStorage(Exception exception)
        => exception is not (StorageRootUnavailableException or StorageUnresponsiveException { IsRefusal: true });

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

        // Through the client also for a storage on disk: a file subject can ask from its load on the worker,
        // and only a call on the client is limited.
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
    /// IStorageContainer - Writes a blob to storage and refreshes the subject of the file.
    /// </summary>
    /// <remarks>
    /// The content is held in memory while it is written. When the subject cannot be refreshed after the write,
    /// the write still succeeds, and the next pass loads the file again.
    /// </remarks>
    public async Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        await WriteBlobCoreAsync(path, buffer.ToArray(), cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Writes a blob to storage and refreshes the subject of the file.
    /// </summary>
    /// <remarks>
    /// The content is copied. When the subject cannot be refreshed after the write, the write still succeeds,
    /// and the next pass loads the file again.
    /// </remarks>
    public Task WriteBlobAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        => WriteBlobCoreAsync(path, content.ToArray(), cancellationToken);

    // The bytes belong to the write and are not changed by anyone else, because a write that was given up still
    // reads them after the caller got its result, see TimeLimitedBlobStorage.
    private async Task WriteBlobCoreAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var connection = Connection;
        var relativePath = StoragePath.Normalize(path);

        await RunOperationAsync(connection, async token =>
        {
            var entry = connection.Index.TryGet(relativePath, out var found) && !found.IsFolder ? found : null;
            if (entry != null)
            {
                // Recorded again only once everything below has succeeded: whatever fails or is given up in
                // between, the next pass then loads the file again.
                entry.Version = default;
                entry.Hash = null;
            }

            await WriteAsync(connection, relativePath, bytes, token);
            _logger?.LogDebug("Wrote blob to storage: {Path}", relativePath);

            // The pass that does the rest is asked for here, because not every storage has a file watcher that
            // would: it loads a file that has no subject or whose subject could not be refreshed, and after a
            // subject was replaced it places an entry that waited for the key this freed.
            var leavesWorkForPass = true;
            try
            {
                leavesWorkForPass = entry?.Subject == null || await connection.Reconciler.RefreshWrittenAsync(entry, bytes, token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                // The bytes are written, so the caller gets no failure.
                _logger?.LogWarning(exception, "Failed to refresh the subject after writing: {Path}", relativePath);
            }
            finally
            {
                if (leavesWorkForPass)
                {
                    connection.NotifyChanged(relativePath);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// IStorageContainer - Deletes the file of a subject and removes the subject from Children.
    /// </summary>
    /// <remarks>
    /// A path without a subject in this storage is left alone and only logged: a folder, an ignored file, or a
    /// file that no pass has loaded yet.
    /// </remarks>
    public async Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
    {
        var connection = Connection;
        var relativePath = StoragePath.Normalize(path);
        await RunOperationAsync(connection, token =>
        {
            if (connection.Index.TryGet(relativePath, out var entry) && entry is { IsFolder: false, Subject: not null })
                return DeleteEntryAsync(connection, entry, token);

            _logger?.LogWarning("Cannot delete blob - no subject at: {Path}", relativePath);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>
    /// Opens the create subject dialog to add a new subject to this storage.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);

    /// <summary>
    /// Gets the names of the children of the root in the order in which they are shown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The storage is not connected.</exception>
    [Query(Title = "Get Order", Description = MetadataMethodDescriptions.GetOrder, Position = 2, Audience = MethodAudience.Agents)]
    public Task<string[]> GetOrderAsync(CancellationToken cancellationToken)
        => GetChildOrderAsync(string.Empty, cancellationToken);

    /// <summary>
    /// Stores the order of the children of the root: the given names first, in the given order, then the other
    /// children in the order in which they are shown.
    /// </summary>
    /// <exception cref="ArgumentException">A name is given more than once, or is not a child of the root.</exception>
    /// <exception cref="InvalidOperationException">The storage is not connected, or the metadata file could not be read.</exception>
    [Operation(Title = "Set Order", Description = MetadataMethodDescriptions.SetOrder, Position = 3, Audience = MethodAudience.Agents)]
    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken)
        => SetChildOrderAsync(string.Empty, names, cancellationToken);

    /// <summary>
    /// Gets the metadata of the children of the root that are stored as JSON files. The root has no metadata of its own.
    /// </summary>
    /// <exception cref="InvalidOperationException">The storage is not connected.</exception>
    [Query(Title = "Get Metadata", Description = MetadataMethodDescriptions.GetMetadata, Position = 4, Audience = MethodAudience.Agents)]
    public Task<ContainerMetadata> GetMetadataAsync(CancellationToken cancellationToken)
        => GetContainerMetadataAsync(string.Empty, cancellationToken);

    /// <summary>
    /// Writes the navigation metadata of a child of the root that is stored as a JSON file. A null or blank value is removed.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not the name of a child stored as a JSON file.</exception>
    /// <exception cref="InvalidOperationException">The storage is not connected, or the metadata file could not be read.</exception>
    [Operation(Title = "Set Child Metadata", Description = MetadataMethodDescriptions.SetChildMetadata, Position = 5, Audience = MethodAudience.Agents)]
    public Task SetChildMetadataAsync(
        string name, string? navigationTitle, string? icon, NavigationLocation? location, AppBarAlignment? alignment,
        CancellationToken cancellationToken)
        => WriteChildMetadataAsync(string.Empty, name, new SubjectMetadata(navigationTitle, icon, location, alignment), cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a subject by finding its path in the index.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is not the subject of a file in this storage.</exception>
    public async Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await RunOperationAsync(connection, token =>
        {
            // A folder has an entry too, but no file that could be deleted.
            if (!connection.Index.TryGetEntry(subject, out var entry) || entry.IsFolder)
                throw new InvalidOperationException("Subject not found in storage registry");

            return DeleteEntryAsync(connection, entry, token);
        }, cancellationToken);
    }

    private async Task DeleteEntryAsync(StorageConnection connection, StorageEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await connection.Client.DeleteAsync(entry.Path, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (CanHaveReachedStorage(exception))
        {
            connection.NotifyChanged(entry.Path);
            throw;
        }

        connection.Index.Remove(entry.Path);
        connection.Reconciler.Apply();
        _subjectFactory.Release(entry.Subject);

        // The key is free now, and an entry that waited for it is only loaded by a pass.
        connection.NotifyChanged(entry.Path);

        _logger?.LogDebug("Deleted blob from storage: {Path}", entry.Path);

        // The metadata of a subject goes with it. A failure here leaves the subject deleted, and only the file behind.
        if (entry.Subject is IConfigurable && StoragePath.IsJson(entry.Path))
        {
            var metadataPath = StorageMetadataPath.GetSubjectFilePath(entry.Path);
            try
            {
                await DeleteMetadataFileAsync(connection, metadataPath, cancellationToken);
                connection.Reconciler.Apply();
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(exception, "Failed to delete the metadata file of a deleted subject: {Path}", metadataPath);
            }
        }
    }

    /// <summary>
    /// Ends the connection for good: a connect after this fails.
    /// </summary>
    public override void Dispose()
    {
        StorageConnection? connection;
        lock (_connectionLock)
        {
            _isDisposed = true;
            Status = StorageStatus.Disconnected;
            connection = _connection;
        }

        // Ended outside the lock, because cancelling it runs code of the work it cancels.
        connection?.Dispose();
        base.Dispose();
    }
}

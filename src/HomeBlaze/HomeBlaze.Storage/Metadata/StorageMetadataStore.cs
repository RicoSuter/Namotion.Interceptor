using System.Buffers;
using System.Text.Json;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// Applies and writes the metadata of a storage, of its folders and of the subjects and properties of its JSON files,
/// for the <see cref="IMetadataStore"/> implementation of the storage. Every write runs on the worker of the storage.
/// </summary>
internal sealed class StorageMetadataStore
{
    // The characters of a path in a metadata file, which a property with metadata cannot have in its name.
    private static readonly SearchValues<char> PathCharacters = SearchValues.Create("/\\[]'");

    private readonly FluentStorageContainer _storage;
    private readonly ILogger? _logger;

    public StorageMetadataStore(FluentStorageContainer storage, ILogger? logger)
    {
        _storage = storage;
        _logger = logger;
        Applier = new StorageMetadataApplier(storage);
    }

    /// <summary>Applies the metadata files of the subjects of the storage.</summary>
    public StorageMetadataApplier Applier { get; }

    /// <inheritdoc cref="IMetadataStore.SetMetadataAsync"/>
    public async Task SetMetadataAsync(IInterceptorSubject subject, string? propertyName, JsonElement values, CancellationToken cancellationToken)
    {
        // A copy, as the worker reads the values later, and the caller can dispose the document that holds them.
        var copy = values.Clone();
        await WriteMetadataAsync(subject, propertyName, () => copy, cancellationToken);
    }

    /// <summary>
    /// Sets metadata of the subject at a path relative to the storage, through the store that keeps it, for agents: see
    /// <see cref="FluentStorageContainer.SetMetadataAsync(string, string?, JsonElement, CancellationToken)"/>.
    /// </summary>
    public Task SetMetadataAsync(string path, string? property, JsonElement values, CancellationToken cancellationToken)
    {
        // An agent can send null for any argument.
        ArgumentNullException.ThrowIfNull(path);
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("values must be a JSON object, for example {\"navTitle\": \"Hall light\"}.", nameof(values));
        }

        var subject = FluentStorageContainer.ResolveMetadataPath(_storage, path)
            ?? throw new ArgumentException(FluentStorageContainer.GetNoSubjectMessage(path));
        var store = subject.FindMetadataStore()
            ?? throw new ArgumentException("The subject is in no storage.", nameof(path));
        return store.SetMetadataAsync(subject, property, values, cancellationToken);
    }

    /// <summary>
    /// Sets the order of a folder of the storage or of its root: see <see cref="MetadataStoreExtensions.SetOrderAsync"/>.
    /// </summary>
    public Task WriteOrderAsync(IInterceptorSubject folder, string[] names, CancellationToken cancellationToken)
        => WriteMetadataAsync(folder, null, () => StorageOrder.Create(folder, names), cancellationToken);

    // The values are made on the worker, so that values that depend on the tree are those of the tree they are written for.
    private async Task WriteMetadataAsync(
        IInterceptorSubject subject, string? propertyName, Func<JsonElement> getValues, CancellationToken cancellationToken)
    {
        var connection = _storage.Connection;
        await FluentStorageContainer.RunOperationAsync(connection, async token =>
        {
            var target = GetMetadataTarget(connection, subject, propertyName);
            var values = getValues();

            // Fresh from the storage, so that an edit that no pass has seen yet is kept.
            var content = await connection.Reconciler.ReadMetadataFileAsync(target.FilePath, token);
            var written = StorageMetadataWriter.Merge(content, target, values);

            // After the writer accepted the values, so that their keys and names can be read.
            if (target.IsContainer && StorageOrder.GetError(subject, values) is { } orderError)
            {
                throw new StorageRefusedException(orderError);
            }

            await WriteMetadataFileAsync(connection, target.FilePath, written, token);
        }, cancellationToken);
    }

    // Runs on the worker. The own keys of the file of the storage or of a folder of it, or the entry of a subject or
    // property in the file beside the stored subject that holds it.
    private StorageMetadataTarget GetMetadataTarget(StorageConnection connection, IInterceptorSubject subject, string? propertyName)
    {
        if (ReferenceEquals(subject, _storage) || subject is VirtualFolder)
        {
            return propertyName is null
                ? new StorageMetadataTarget(StorageMetadataPath.GetFolderFilePath(GetFolderPath(connection, subject)), null, null, IsContainer: true)
                : throw new StorageRefusedException("A folder and the root of a storage have metadata of their own, but their properties have none.");
        }

        if (subject.TryGetRegisteredSubject() is not { } registered ||
            !Applier.TryFindStoredSubject(registered, out var storedSubject) ||
            !connection.Index.TryGetEntry(storedSubject.Subject, out var entry) ||
            entry is not { IsPlaced: true, HasSidecar: true, Subject: not IMetadataStore })
        {
            throw new StorageRefusedException(
                "Only a storage, its folders, the subjects of its JSON files and the subjects and properties inside them have metadata.");
        }

        if (propertyName is not null && (propertyName == KnownAttributes.Metadata || registered.TryGetProperty(propertyName) is not { IsAttribute: false }))
        {
            throw new StorageRefusedException(FluentStorageContainer.GetNoPropertyMessage(propertyName));
        }

        // A path in a metadata file cannot hold them, so the entry would not apply or would be that of another property.
        if (propertyName is not null && propertyName.AsSpan().ContainsAny(PathCharacters))
        {
            throw new StorageRefusedException(
                $"The property {NameList.Format([propertyName])} cannot have metadata: its name has a character of a path (/, \\, [, ] or ').");
        }

        var filePath = StorageMetadataPath.GetSubjectFilePath(entry.Path);
        var path = MetadataPathKey.GetPath(registered, storedSubject)
            ?? throw new StorageRefusedException("The subject cannot be reached from the subject of its file.");
        if (propertyName is not null)
        {
            return new StorageMetadataTarget(filePath, StorageMetadataFile.PropertiesKey, MetadataPathKey.GetPropertyKey(path, propertyName), IsContainer: false);
        }

        return path.Length == 0
            ? new StorageMetadataTarget(filePath, null, null, IsContainer: false)
            : new StorageMetadataTarget(filePath, StorageMetadataFile.SubjectsKey, path, IsContainer: false);
    }

    // Runs on the worker. Empty for the storage, else the path of the folder. Only the placed folder counts, so that a
    // folder that was deleted cannot write into a folder of the same name that was created after it.
    private string GetFolderPath(StorageConnection connection, IInterceptorSubject owner)
    {
        if (ReferenceEquals(owner, _storage))
        {
            return string.Empty;
        }

        var folder = (VirtualFolder)owner;
        var path = StoragePath.Normalize(folder.RelativePath);
        if (!connection.Index.TryGet(path, out var entry) || entry is not { IsFolder: true, IsPlaced: true, Subject: VirtualFolder placedFolder })
        {
            throw new StorageRefusedException($"No folder at '{path}'.");
        }

        return ReferenceEquals(placedFolder, folder)
            ? path
            : throw new StorageRefusedException($"The folder '{path}' is no longer in the storage.");
    }

    // Runs on the worker. No content deletes the file, so that a metadata file exists only while it holds something.
    private async Task WriteMetadataFileAsync(
        StorageConnection connection, string path, (byte[] Content, StorageMetadataFile File)? written, CancellationToken cancellationToken)
    {
        if (written is null)
        {
            await DeleteMetadataFileAsync(connection, path, cancellationToken);
            connection.Reconciler.Apply();
            _logger?.LogDebug("Removed empty metadata from storage: {Path}", path);
            return;
        }

        var (content, file) = written.Value;
        var entry = new StorageEntry { Path = path, IsFolder = false, Metadata = file };
        await _storage.WriteAndRecordAsync(connection, entry, content, cancellationToken);
        connection.Index.Set(entry);
        connection.Reconciler.Apply();
        _logger?.LogDebug("Wrote metadata to storage: {Path}", path);
    }

    /// <summary>
    /// Deletes the metadata file of a subject whose file was deleted, also when the caller has given up by now, as
    /// nothing would delete it later. A failure is only logged, so the delete succeeds or fails as it did without it.
    /// Called on the worker.
    /// </summary>
    /// <param name="connection">The connection that deleted the file.</param>
    /// <param name="entry">The entry of the deleted file.</param>
    /// <param name="isFileKnownGone">
    /// False when the delete failed, so that the metadata file is only deleted once the file turns out to be gone. True
    /// leaves removing the entry from the index and applying it to the caller.
    /// </param>
    public async Task DeleteSidecarAsync(StorageConnection connection, StorageEntry entry, bool isFileKnownGone)
    {
        if (!entry.HasSidecar)
        {
            return;
        }

        try
        {
            // A file in a folder that is gone is gone with its metadata file.
            if (!isFileKnownGone &&
                (await connection.Reconciler.IsFolderGoneAsync(entry.Parent, CancellationToken.None) ||
                 await connection.Client.ExistsAsync(entry.Path, CancellationToken.None)))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to find out whether the file of a subject is gone, so its metadata file is kept: {Path}", entry.Path);
            return;
        }

        var metadataPath = StorageMetadataPath.GetSubjectFilePath(entry.Path);
        try
        {
            await DeleteMetadataFileAsync(connection, metadataPath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to delete the metadata file of a deleted subject: {Path}", metadataPath);
            return;
        }

        // A known gone file is removed from the index by the caller, which applies both at once.
        if (!isFileKnownGone)
        {
            try
            {
                connection.Reconciler.Apply();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Failed to apply the deleted metadata file of a deleted subject: {Path}", metadataPath);
            }
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
        catch (Exception exception) when (FluentStorageContainer.CanHaveReachedStorage(exception))
        {
            connection.NotifyChanged(path);
            throw;
        }

        connection.Index.Remove(path);
    }
}

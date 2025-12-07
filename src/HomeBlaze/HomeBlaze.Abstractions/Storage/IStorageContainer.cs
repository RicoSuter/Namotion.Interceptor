namespace HomeBlaze.Abstractions.Storage;

/// <summary>
/// Interface for storage backends that can read/write blobs.
/// </summary>
public interface IStorageContainer
{
    /// <summary>
    /// Gets the current status of the storage connection.
    /// </summary>
    StorageStatus Status { get; }

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    Task<Stream> ReadBlobAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes a blob to storage.
    /// </summary>
    Task WriteBlobAsync(string path, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    Task DeleteBlobAsync(string path, CancellationToken ct = default);
}

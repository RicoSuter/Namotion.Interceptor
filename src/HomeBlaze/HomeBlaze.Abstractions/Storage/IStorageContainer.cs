namespace HomeBlaze.Abstractions.Storage;

/// <summary>
/// Metadata about a blob in storage.
/// </summary>
public record BlobMetadata(long Size, DateTime? LastModifiedUtc);

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
    /// Gets metadata about a blob (size, last modified, etc.).
    /// </summary>
    Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken ct = default);

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

using Namotion.Interceptor;

namespace HomeBlaze.Storage.Abstractions;

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
    Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a blob to storage.
    /// </summary>
    Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    Task DeleteBlobAsync(string path, CancellationToken cancellationToken);
    
    // TODO: Update and align all xml docs in this interface

    /// <summary>
    /// Adds a new subject to storage. Serializes the subject, writes the file, and updates the hierarchy.
    /// </summary>
    /// <param name="path">The file name (e.g., "mysubject.json")</param>
    /// <param name="subject">The subject to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a subject from storage. Removes the file and updates the hierarchy.
    /// </summary>
    /// <param name="subject">The subject to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken);
    // TODO: Do we need to add path here as well for symmetry with AddSubjectAsync? will this help delete? or not necessary?
}

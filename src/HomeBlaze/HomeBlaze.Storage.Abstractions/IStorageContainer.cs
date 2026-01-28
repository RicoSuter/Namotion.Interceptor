using Namotion.Interceptor;

namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Metadata about a blob in storage.
/// </summary>
public record BlobMetadata(long Size, DateTime? LastModifiedUtc);

/// <summary>
/// Interface for storage containers that manage subjects and their persistence.
/// Provides blob-level operations and subject lifecycle management.
/// </summary>
public interface IStorageContainer
{
    /// <summary>
    /// Gets the current status of the storage connection.
    /// </summary>
    StorageStatus Status { get; }

    /// <summary>
    /// Gets metadata about a blob at the specified path.
    /// </summary>
    /// <param name="path">The relative path to the blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The blob metadata, or null if the blob does not exist.</returns>
    Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a blob from storage as a stream.
    /// </summary>
    /// <param name="path">The relative path to the blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A stream containing the blob content.</returns>
    Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes content to a blob at the specified path.
    /// </summary>
    /// <param name="path">The relative path to the blob.</param>
    /// <param name="content">The content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    /// <param name="path">The relative path to the blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteBlobAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a new subject to storage. Serializes the subject, writes the file, and registers it in the hierarchy.
    /// </summary>
    /// <param name="path">The relative path for the subject file (e.g., "mysubject.json").</param>
    /// <param name="subject">The subject to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a subject from storage. Removes the file and unregisters it from the hierarchy.
    /// The path is resolved from the internal registry - subjects must be registered before deletion.
    /// </summary>
    /// <param name="subject">The subject to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken);
    // TODO: Do we need to add path in DeleteSubjectAsync as well for symmetry with AddSubjectAsync? will this help delete? or not necessary?
}

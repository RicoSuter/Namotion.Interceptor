namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Interface for file subjects in storage.
/// </summary>
public interface IStorageFile
{
    /// <summary>
    /// Gets the file name including extension.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the full path relative to the storage container.
    /// </summary>
    string FullPath { get; }

    /// <summary>
    /// Gets the storage container this file belongs to.
    /// </summary>
    IStorageContainer Storage { get; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    long FileSize { get; set; }

    /// <summary>
    /// Gets or sets the last modification time (UTC).
    /// </summary>
    DateTime LastModified { get; set; }

    /// <summary>
    /// Reads the file content.
    /// </summary>
    Task<Stream> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes content to the file.
    /// </summary>
    Task WriteAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Callback when the file changes on disk.
    /// Implementation decides whether to reload content or do nothing.
    /// </summary>
    /// <remarks>
    /// A storage can call this from the work that changes its hierarchy. The implementation may read from the
    /// storage, but must not call an operation that changes it (<see cref="IStorageContainer.WriteBlobAsync"/>,
    /// <see cref="IStorageContainer.DeleteBlobAsync"/>, <see cref="IStorageContainer.AddSubjectAsync"/>,
    /// <see cref="IStorageContainer.DeleteSubjectAsync"/>, saving configuration): the storage refuses such a
    /// call with an <see cref="InvalidOperationException"/>, also from a task that the implementation starts
    /// and does not await, unless that task is started without the execution flow.
    /// </remarks>
    Task OnFileChangedAsync(CancellationToken cancellationToken);
}

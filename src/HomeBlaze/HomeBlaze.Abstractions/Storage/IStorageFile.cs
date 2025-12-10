namespace HomeBlaze.Abstractions.Storage;

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
    Task OnFileChangedAsync(CancellationToken cancellationToken);
}

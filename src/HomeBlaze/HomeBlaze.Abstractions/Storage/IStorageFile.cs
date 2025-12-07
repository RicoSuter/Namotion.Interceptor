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
    /// Reads the file content.
    /// </summary>
    Task<Stream> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes content to the file.
    /// </summary>
    Task WriteAsync(Stream content, CancellationToken ct = default);
}

using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping for virtual folders. Implements IStorageContainer by delegation to parent.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : ITitleProvider, IIconProvider, IStorageContainer
{
    /// <summary>
    /// Reference to the root storage.
    /// </summary>
    public IStorageContainer Storage { get; }

    /// <summary>
    /// Relative path within the storage (e.g., "folder/subfolder/").
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Child subjects (files and folders).
    /// </summary>
    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public string? Title => Path.GetFileName(RelativePath.TrimEnd('/'));

    public string Icon => "Folder";

    public StorageStatus Status => Storage.Status;

    public VirtualFolder(IStorageContainer storage, string relativePath)
    {
        Storage = storage;
        RelativePath = relativePath;
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    /// <summary>
    /// Gets metadata about a blob. Path is relative to the storage root.
    /// </summary>
    public Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken)
        => Storage.GetBlobMetadataAsync(path, cancellationToken);

    /// <summary>
    /// Reads a blob from storage. Path is relative to the storage root.
    /// </summary>
    public Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(path, cancellationToken);

    /// <summary>
    /// Writes a blob to storage. Path is relative to the storage root.
    /// </summary>
    public Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(path, content, cancellationToken);

    /// <summary>
    /// Deletes a blob from storage and removes it from Children.
    /// Path is relative to the storage root.
    /// </summary>
    public async Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
    {
        // Delete from storage
        await Storage.DeleteBlobAsync(path, cancellationToken);

        // Remove from Children directly (don't rely on FileWatcher)
        var fileName = Path.GetFileName(path);
        if (Children.ContainsKey(fileName))
        {
            var children = new Dictionary<string, IInterceptorSubject>(Children);
            children.Remove(fileName);
            Children = children;
        }
    }
}

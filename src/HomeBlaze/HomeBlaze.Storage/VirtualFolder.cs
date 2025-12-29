using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Authorization;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

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
    [InlinePaths]
    [State]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public string Title => Path.GetFileName(RelativePath.TrimEnd('/'));

    public string IconName => "Folder";

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
    /// Deletes a blob from storage. Path is relative to the storage root.
    /// The parent storage handles removal from the hierarchy.
    /// </summary>
    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
        => Storage.DeleteBlobAsync(path, cancellationToken);

    /// <summary>
    /// Adds a subject to this folder.
    /// </summary>
    public Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var fullPath = string.IsNullOrEmpty(RelativePath)
            ? path
            : Path.Combine(RelativePath, path);

        return Storage.AddSubjectAsync(fullPath, subject, cancellationToken);
    }

    /// <summary>
    /// Deletes a subject from this folder.
    /// </summary>
    public Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
        => Storage.DeleteSubjectAsync(subject, cancellationToken);

    /// <summary>
    /// Opens the create subject wizard to add a new subject to this folder.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    [SubjectMethodAuthorize("Admin")]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);
}

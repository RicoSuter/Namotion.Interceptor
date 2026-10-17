using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping for virtual folders. Implements IStorageContainer by delegation to parent.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : ITitleProvider, IIconProvider, IStorageContainer, IMetadataContainer
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

    /// <inheritdoc />
    public partial FolderMetadata? Metadata { get; private set; }

    /// <inheritdoc />
    public partial ImmutableArray<string> ChildOrder { get; private set; }

    /// <inheritdoc />
    public bool HasOwnMetadata => true;

    [Derived]
    public string Title => Metadata?.Title ?? Path.GetFileName(RelativePath.TrimEnd('/'));

    [Derived]
    public string IconName => Metadata?.Icon ?? "Folder";

    public StorageStatus Status => Storage.Status;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ChildNames => [.. Children.Keys];

    public VirtualFolder(IStorageContainer storage, string relativePath)
    {
        Storage = storage;
        RelativePath = relativePath;
        Children = new Dictionary<string, IInterceptorSubject>();
        ChildOrder = [];
    }

    /// <summary>
    /// Takes the metadata and the order from the <c>.metadata.json</c> of the folder, or none without one. Called by
    /// the storage on its worker.
    /// </summary>
    internal void ApplyMetadata(StorageMetadataDocument? document)
    {
        var metadata = document?.ToFolderMetadata();
        if (metadata != Metadata)
        {
            Metadata = metadata;
        }

        var childOrder = document?.ToChildOrder() ?? [];
        if (!childOrder.SequenceEqual(ChildOrder))
        {
            ChildOrder = childOrder;
        }
    }

    /// <inheritdoc />
    public Task WriteMetadataAsync(FolderMetadata? metadata, CancellationToken cancellationToken)
        => RootStorage.WriteFolderMetadataAsync(StoragePath.Normalize(RelativePath), metadata, cancellationToken);

    /// <inheritdoc />
    public Task WriteChildMetadataAsync(IInterceptorSubject child, SubjectMetadata? metadata, CancellationToken cancellationToken)
        => RootStorage.WriteSubjectMetadataAsync(child, metadata, cancellationToken);

    /// <inheritdoc />
    public Task MoveChildAsync(string name, string targetName, CancellationToken cancellationToken)
        => RootStorage.MoveChildAsync(StoragePath.Normalize(RelativePath), name, targetName, cancellationToken);

    private FluentStorageContainer RootStorage => Storage as FluentStorageContainer
        ?? throw new NotSupportedException("Only a folder of a FluentStorageContainer stores metadata.");

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
    /// Writes a blob to storage. Path is relative to the storage root.
    /// </summary>
    public Task WriteBlobAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
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
    /// Opens the create subject dialog to add a new subject to this folder.
    /// </summary>
    [Operation(Title = "Create", Icon = "Add", Position = 1)]
    public Task CreateAsync([FromServices] ISubjectSetupService subjectSetupService, CancellationToken cancellationToken)
        => subjectSetupService.CreateSubjectAndAddToStorageAsync(this, cancellationToken);
}

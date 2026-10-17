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

    /// <summary>
    /// Gets the names of the children of this folder in the order in which they are shown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The storage is not connected, or the folder is not in it.</exception>
    [Query(Title = "Get Order", Description = MetadataMethodDescriptions.GetOrder, Position = 2, Audience = MethodAudience.Agents)]
    public Task<string[]> GetOrderAsync(CancellationToken cancellationToken)
        => RootStorage.GetChildOrderAsync(StoragePath.Normalize(RelativePath), cancellationToken);

    /// <summary>
    /// Stores the order of the children of this folder: the given names first, in the given order, then the other
    /// children in the order in which they are shown.
    /// </summary>
    /// <exception cref="ArgumentException">A name is given more than once, or is not a child of this folder.</exception>
    /// <exception cref="InvalidOperationException">
    /// The storage is not connected, the folder is not in it, or its metadata file could not be read.
    /// </exception>
    [Operation(Title = "Set Order", Description = MetadataMethodDescriptions.SetOrder, Position = 3, Audience = MethodAudience.Agents)]
    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken)
        => RootStorage.SetChildOrderAsync(StoragePath.Normalize(RelativePath), names, cancellationToken);

    /// <summary>
    /// Gets the metadata of this folder and of its children that are stored as JSON files.
    /// </summary>
    /// <exception cref="InvalidOperationException">The storage is not connected, or the folder is not in it.</exception>
    [Query(Title = "Get Metadata", Description = MetadataMethodDescriptions.GetMetadata, Position = 4, Audience = MethodAudience.Agents)]
    public Task<ContainerMetadata> GetMetadataAsync(CancellationToken cancellationToken)
        => RootStorage.GetContainerMetadataAsync(StoragePath.Normalize(RelativePath), cancellationToken);

    /// <summary>
    /// Writes the metadata of this folder. A null or blank value is removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The storage is not connected, the folder is not in it, or its metadata file could not be read.
    /// </exception>
    [Operation(Title = "Set Metadata", Description = MetadataMethodDescriptions.SetMetadata, Position = 5, Audience = MethodAudience.Agents)]
    public Task SetMetadataAsync(string? title, string? navigationTitle, string? icon, CancellationToken cancellationToken)
        => WriteMetadataAsync(new FolderMetadata(title, navigationTitle, icon), cancellationToken);

    /// <summary>
    /// Writes the navigation metadata of a child of this folder that is stored as a JSON file. A null or blank value is removed.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not the name of a child stored as a JSON file.</exception>
    /// <exception cref="InvalidOperationException">
    /// The storage is not connected, the folder is not in it, or the metadata file could not be read.
    /// </exception>
    [Operation(Title = "Set Child Metadata", Description = MetadataMethodDescriptions.SetChildMetadata, Position = 6, Audience = MethodAudience.Agents)]
    public Task SetChildMetadataAsync(
        string name, string? navigationTitle, string? icon, NavigationLocation? location, AppBarAlignment? alignment,
        CancellationToken cancellationToken)
        => RootStorage.WriteChildMetadataAsync(
            StoragePath.Normalize(RelativePath), name, new SubjectMetadata(navigationTitle, icon, location, alignment), cancellationToken);
}

using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
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
    public partial ImmutableArray<string> ChildOrder { get; private set; }

    // Title and IconName read the metadata dictionary and must not be [Derived]: reading the dynamic metadata property
    // bypasses interception, so derived tracking would not fire. Observers watch HomeBlaze:Metadata instead.

    /// <summary>Gets the <c>title</c> of the metadata of the folder, else the name of the directory.</summary>
    public string Title => this.GetMetadata().GetString(MetadataKeys.Title) ?? Path.GetFileName(RelativePath.TrimEnd('/'));

    /// <summary>Gets the <c>icon</c> of the metadata of the folder, else <c>Folder</c>.</summary>
    public string IconName => this.GetMetadata().GetString(MetadataKeys.Icon) ?? "Folder";

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
    internal void ApplyMetadata(StorageMetadataFile? file)
    {
        if (StorageMetadataValues.SetOwnMetadata(this, ChildOrder, file) is { } childOrder)
        {
            ChildOrder = childOrder;
        }
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

    /// <summary>Gets the names of the children of this folder in the order in which they are shown, for agents.</summary>
    [Query(Title = "Get Order", Description = StorageMetadataMethods.GetOrderDescription, Position = 3)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public string[] GetOrder() => ChildOrdering.GetShownNames(this);

    /// <summary>
    /// Sets the order of the children of this folder, for agents: see <see cref="MetadataStoreExtensions.SetOrderAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A name is refused, or the write is refused; the message says why.</exception>
    [Operation(Title = "Set Order", Description = StorageMetadataMethods.SetOrderDescription, Position = 4)]
    [ExcludeFrom(ExclusionTarget.UserInterface)]
    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken)
        => StorageMetadataMethods.RunAsync(() => MetadataStoreExtensions.SetOrderAsync(this, names, cancellationToken));

    /// <summary>
    /// Creates a Markdown file in this folder and adds it as a page.
    /// </summary>
    /// <inheritdoc cref="FluentStorageContainer.CreateMarkdownFileAsync(string, string, CancellationToken)" path="/exception"/>
    /// <exception cref="ArgumentException">The storage of this folder is not a <see cref="FluentStorageContainer"/>.</exception>
    [Operation(Title = "Create Markdown File", Description = "Creates a new markdown file with the given name (ending in .md) and content in this folder.", Icon = "NoteAdd", Position = 2)]
    public Task CreateMarkdownFileAsync(string name, string content, CancellationToken cancellationToken)
        => Storage is FluentStorageContainer storage
            ? storage.CreateMarkdownFileAsync(RelativePath, name, content, cancellationToken)
            // An argument error, so that its message reaches the caller of the operation.
            : throw new ArgumentException("Markdown files cannot be created in this folder: its storage does not support it.");
}

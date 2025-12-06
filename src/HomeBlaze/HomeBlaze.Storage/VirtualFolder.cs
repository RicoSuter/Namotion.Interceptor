using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping that delegates operations to parent Storage.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : ITitleProvider, IIconProvider
{
    // MudBlazor Icons.Material.Filled.Folder
    private const string FolderIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z\" /></svg>";

    /// <summary>
    /// Reference to the root storage.
    /// </summary>
    public FluentStorageContainer Storage { get; }

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

    public string Icon => FolderIcon;

    public VirtualFolder(IInterceptorSubjectContext context, FluentStorageContainer storage, string relativePath)
    {
        Storage = storage;
        RelativePath = relativePath;
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    private string ResolvePath(string name) => RelativePath + name;

    /// <summary>
    /// Adds a new subject to storage at this folder's path.
    /// </summary>
    public Task AddSubjectAsync(string name, IInterceptorSubject subject, CancellationToken ct = default)
        => Storage.AddSubjectAsync(ResolvePath(name), subject, ct);

    /// <summary>
    /// Deletes a subject from storage.
    /// </summary>
    public Task DeleteSubjectAsync(string name, CancellationToken ct = default)
        => Storage.DeleteSubjectAsync(ResolvePath(name), ct);

    /// <summary>
    /// Writes a blob to storage (upsert semantics).
    /// </summary>
    public Task WriteBlobAsync(string name, Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(ResolvePath(name), content, ct);

    /// <summary>
    /// Deletes a blob from storage.
    /// </summary>
    public Task DeleteBlobAsync(string name, CancellationToken ct = default)
        => Storage.DeleteBlobAsync(ResolvePath(name), ct);

    /// <summary>
    /// Reads a blob from storage.
    /// </summary>
    public Task<Stream> ReadBlobAsync(string name, CancellationToken ct = default)
        => Storage.ReadBlobAsync(ResolvePath(name), ct);
}

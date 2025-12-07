using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping for virtual folders. Does NOT implement IStorageContainer.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : IDisplaySubject
{
    private const string FolderIcon = Icons.Material.Filled.Folder;
        
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

    public string Icon => FolderIcon;

    public VirtualFolder(IInterceptorSubjectContext context, IStorageContainer storage, string relativePath)
    {
        Storage = storage;
        RelativePath = relativePath;
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}

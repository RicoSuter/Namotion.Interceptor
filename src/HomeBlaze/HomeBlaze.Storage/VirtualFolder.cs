using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Hierarchical grouping for virtual folders. Does NOT implement IStorageContainer.
/// </summary>
[InterceptorSubject]
public partial class VirtualFolder : IDisplaySubject
{
    private const string FolderIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z\" /></svg>";

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

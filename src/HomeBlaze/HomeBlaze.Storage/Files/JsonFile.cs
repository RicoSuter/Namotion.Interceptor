using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a JSON file in storage (non-configurable JSON).
/// </summary>
[InterceptorSubject]
public partial class JsonFile : ITitleProvider, IIconProvider, IStorageItem, IPersistentSubject
{
    // MudBlazor Icons.Material.Filled.DataObject
    private const string JsonIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M5,3H7V5H5V10A2,2 0 0,1 3,12A2,2 0 0,1 5,14V19H7V21H5C3.93,20.73 3,20.1 3,19V15A2,2 0 0,0 1,13H0V11H1A2,2 0 0,0 3,9V5A2,2 0 0,1 5,3M19,3A2,2 0 0,1 21,5V9A2,2 0 0,0 23,11H24V13H23A2,2 0 0,0 21,15V19A2,2 0 0,1 19,21H17V19H19V14A2,2 0 0,1 21,12A2,2 0 0,1 19,10V5H17V3H19Z\" /></svg>";

    /// <summary>
    /// Reference to the root storage (optional, for FluentStorage-based files).
    /// </summary>
    public FluentStorageContainer? Storage { get; }

    /// <summary>
    /// Path within the storage (for FluentStorage-based files).
    /// </summary>
    public string? BlobPath { get; }

    /// <summary>
    /// Full file path (for filesystem-based files).
    /// </summary>
    public partial string FilePath { get; set; }

    /// <summary>
    /// Name of the file including extension.
    /// </summary>
    public partial string FileName { get; set; }

    /// <summary>
    /// JSON content of the file.
    /// </summary>
    [State]
    public partial string Content { get; set; }

    public string? Title => Path.GetFileNameWithoutExtension(BlobPath ?? FilePath);

    public string Icon => JsonIcon;

    /// <summary>
    /// Creates a JsonFile for FluentStorage-based files.
    /// </summary>
    public JsonFile(IInterceptorSubjectContext context, FluentStorageContainer storage, string blobPath)
    {
        Storage = storage;
        BlobPath = blobPath;
        FilePath = blobPath;
        FileName = Path.GetFileName(blobPath);
        Content = string.Empty;
    }

    /// <summary>
    /// Creates a JsonFile for filesystem-based files.
    /// </summary>
    public JsonFile()
    {
        FilePath = string.Empty;
        FileName = string.Empty;
        Content = string.Empty;
    }

    /// <summary>
    /// Saves the JSON content to storage.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (Storage != null && !string.IsNullOrEmpty(BlobPath))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Content));
            await Storage.WriteBlobAsync(BlobPath, stream, ct);
        }
        else if (!string.IsNullOrEmpty(FilePath))
        {
            await File.WriteAllTextAsync(FilePath, Content, ct);
        }
    }

    /// <summary>
    /// IPersistentSubject implementation - reloads content from file.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (Storage != null && !string.IsNullOrEmpty(BlobPath))
        {
            Content = await Storage.ReadBlobTextAsync(BlobPath, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
        {
            Content = await File.ReadAllTextAsync(FilePath, cancellationToken);
        }
    }
}

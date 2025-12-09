using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using MudBlazor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a JSON file in storage (non-configurable JSON).
/// </summary>
[InterceptorSubject]
public partial class JsonFile : IStorageFile, ITitleProvider, IIconProvider
{
    public string? Title => Path.GetFileNameWithoutExtension(FullPath);

    public string Icon => Icons.Material.Filled.DataObject;
    
    public IStorageContainer Storage { get; }
    
    public string FullPath { get; }
    
    public string Name { get; }
    
    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Order = 1)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Order = 2)]
    public partial DateTime LastModified { get; set; }

    public JsonFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken = default)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken = default)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Update metadata
        try
        {
            if (Storage is FluentStorageContainer container)
            {
                var fileInfo = new FileInfo(Path.Combine(container.ConnectionString, FullPath));
                FileSize = fileInfo.Length;
                LastModified = fileInfo.LastWriteTimeUtc;
            }
        }
        catch { /* Ignore metadata errors */ }

        return Task.CompletedTask;
    }
}

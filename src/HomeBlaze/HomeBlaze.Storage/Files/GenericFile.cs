using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using MudBlazor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a generic file in storage.
/// </summary>
[InterceptorSubject]
public partial class GenericFile : IStorageFile, ITitleProvider, IIconProvider
{
    public string? Title => Name;

    public string Icon => Icons.Material.Filled.InsertDriveFile;

    public IStorageContainer Storage { get; }
    
    public string FullPath { get; }

    public string Name { get; }

    /// <summary>
    /// File extension including the dot.
    /// </summary>
    [State(Order = 1)]
    public partial string Extension { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Order = 2)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Order = 3)]
    public partial DateTime LastModified { get; set; }

    public GenericFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;

        Name = Path.GetFileName(fullPath);
        Extension = Path.GetExtension(fullPath);
    }

    public Task<Stream> ReadAsync(CancellationToken ct = default)
        => Storage.ReadBlobAsync(FullPath, ct);

    public Task WriteAsync(Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(FullPath, content, ct);
}

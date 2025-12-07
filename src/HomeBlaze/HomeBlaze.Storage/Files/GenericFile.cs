using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a generic file in storage.
/// </summary>
[InterceptorSubject]
public partial class GenericFile : IStorageFile, IDisplaySubject
{
    private const string FileIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M6,2A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2H6M13,3.5L18.5,9H13V3.5Z\" /></svg>";

    public string Icon => FileIcon;

    public string? Title => Name;

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

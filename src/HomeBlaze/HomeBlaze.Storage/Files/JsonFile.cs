using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a JSON file in storage (non-configurable JSON).
/// </summary>
[InterceptorSubject]
public partial class JsonFile : IStorageFile, IDisplaySubject
{
    private const string JsonIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M5,3H7V5H5V10A2,2 0 0,1 3,12A2,2 0 0,1 5,14V19H7V21H5C3.93,20.73 3,20.1 3,19V15A2,2 0 0,0 1,13H0V11H1A2,2 0 0,0 3,9V5A2,2 0 0,1 5,3M19,3A2,2 0 0,1 21,5V9A2,2 0 0,0 23,11H24V13H23A2,2 0 0,0 21,15V19A2,2 0 0,1 19,21H17V19H19V14A2,2 0 0,1 21,12A2,2 0 0,1 19,10V5H17V3H19Z\" /></svg>";

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    public string? Title => Path.GetFileNameWithoutExtension(FullPath);
    public string Icon => JsonIcon;

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

    public Task<Stream> ReadAsync(CancellationToken ct = default)
        => Storage.ReadBlobAsync(FullPath, ct);

    public Task WriteAsync(Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(FullPath, content, ct);
}

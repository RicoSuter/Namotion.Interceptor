using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a generic file in storage.
/// </summary>
[InterceptorSubject]
public partial class GenericFile : IStorageFile, ITitleProvider, IIconProvider
{
    public string? Title => Name;

    public string Icon => "InsertDriveFile";

    public IStorageContainer Storage { get; }
    
    public string FullPath { get; }

    public string Name { get; }

    /// <summary>
    /// File extension including the dot.
    /// </summary>
    [State(Position = 1)]
    public partial string Extension { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Position = 2)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Position = 3)]
    public partial DateTime LastModified { get; set; }

    public GenericFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;

        Name = Path.GetFileName(fullPath);
        Extension = Path.GetExtension(fullPath);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
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

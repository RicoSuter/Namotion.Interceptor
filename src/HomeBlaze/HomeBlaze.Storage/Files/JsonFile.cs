using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a JSON file in storage (non-configurable JSON).
/// </summary>
[InterceptorSubject]
public partial class JsonFile : IStorageFile, ITitleProvider, IIconProvider
{
    public string? Title => Path.GetFileNameWithoutExtension(FullPath);

    public string IconName => "DataObject";
    
    public IStorageContainer Storage { get; }
    
    public string FullPath { get; }
    
    public string Name { get; }
    
    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Unit = StateUnit.Byte, Position = 1)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Position = 2)]
    public partial DateTime LastModified { get; set; }

    public JsonFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    /// <summary>
    /// Writes the content to the file. Plaintext secrets in the content are stored in the secret store and written as
    /// references, so the written content can differ from the given one.
    /// </summary>
    /// <exception cref="IOException">The secret store cannot be written. Nothing is written.</exception>
    /// <exception cref="UnauthorizedAccessException">The secret store cannot be written. Nothing is written.</exception>
    /// <exception cref="InvalidOperationException">A type in the content has a secret configuration property that is
    /// not a string property of a subject. Nothing is written.</exception>
    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    /// <summary>
    /// Does nothing: the file holds only size and time, which the storage sets from its listing.
    /// </summary>
    public Task OnFileChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

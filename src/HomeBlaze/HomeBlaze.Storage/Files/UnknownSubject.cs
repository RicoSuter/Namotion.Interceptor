using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Stands in for a JSON file whose <c>$type</c> cannot be created, either because the type is not loaded
/// or because creating it failed. The file is never written by the configuration writer, and the storage
/// replaces this subject with the real one once its type can be created.
/// </summary>
[InterceptorSubject]
public partial class UnknownSubject : IStorageFile, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The reason used when no loaded type has the file's <c>$type</c> name.
    /// </summary>
    public const string TypeNotLoadedReason = "Type is not loaded.";

    public string? Title => Path.GetFileNameWithoutExtension(FullPath);

    public string IconName => "Warning";

    public string IconColor => "Warning";

    public IStorageContainer Storage { get; }

    public string FullPath { get; }

    public string Name { get; }

    /// <summary>
    /// The <c>$type</c> value of the file.
    /// </summary>
    [State("Type", Position = 1)]
    public partial string TypeName { get; internal set; }

    /// <summary>
    /// Why the file is not the real subject.
    /// </summary>
    [State(Position = 2)]
    public partial string Reason { get; internal set; }

    [State("Size", Position = 3)]
    public partial long FileSize { get; set; }

    [State("Modified", Position = 4)]
    public partial DateTime LastModified { get; set; }

    public UnknownSubject(IStorageContainer storage, string fullPath, string typeName, string reason)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        TypeName = typeName;
        Reason = reason;
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    // The storage recreates the subject on a file change, so there is no in-memory content to refresh.
    public Task OnFileChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

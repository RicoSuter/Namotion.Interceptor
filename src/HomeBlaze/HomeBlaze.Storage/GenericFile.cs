using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Represents a generic file in storage.
/// Binary content is accessed via methods to avoid tracking overhead.
/// </summary>
[InterceptorSubject]
public partial class GenericFile : IIconProvider, IStorageItem, ITitleProvider
{
    // MudBlazor Icons.Material.Filled.InsertDriveFile
    private const string FileIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M6,2A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2H6M13,3.5L18.5,9H13V3.5Z\" /></svg>";

    public string Icon => FileIcon;
    public string? Title => FileName;

    /// <summary>
    /// Full path to the file.
    /// </summary>
    public partial string FilePath { get; set; }

    /// <summary>
    /// Name of the file including extension.
    /// </summary>
    public partial string FileName { get; set; }

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

    public GenericFile()
    {
        FilePath = string.Empty;
        FileName = string.Empty;
        Extension = string.Empty;
    }

    /// <summary>
    /// Gets the file content as bytes.
    /// </summary>
    public async Task<byte[]> GetBytesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return Array.Empty<byte>();

        return await File.ReadAllBytesAsync(FilePath, cancellationToken);
    }

    /// <summary>
    /// Opens a read stream for the file.
    /// </summary>
    public Stream OpenRead()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            throw new FileNotFoundException("File not found", FilePath);

        return File.OpenRead(FilePath);
    }

    /// <summary>
    /// Writes bytes to the file.
    /// </summary>
    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(FilePath))
            throw new InvalidOperationException("FilePath is not set");

        await File.WriteAllBytesAsync(FilePath, data, cancellationToken);

        // Update metadata
        var fileInfo = new FileInfo(FilePath);
        FileSize = fileInfo.Length;
        LastModified = fileInfo.LastWriteTimeUtc;
    }
}

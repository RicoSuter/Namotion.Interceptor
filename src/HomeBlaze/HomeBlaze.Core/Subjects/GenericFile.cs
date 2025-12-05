using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Core.Subjects;

/// <summary>
/// Represents a generic file in storage.
/// Binary content is accessed via methods to avoid tracking overhead.
/// </summary>
[InterceptorSubject]
public partial class GenericFile
{
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
    public partial string Extension { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
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

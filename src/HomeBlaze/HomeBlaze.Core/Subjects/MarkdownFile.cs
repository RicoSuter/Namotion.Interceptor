using HomeBlaze.Core.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Core.Subjects;

/// <summary>
/// Represents a Markdown file in storage.
/// Content is accessed via methods to avoid tracking overhead.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile
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
    /// File size in bytes.
    /// </summary>
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    public partial DateTime LastModified { get; set; }

    public MarkdownFile()
    {
        FilePath = string.Empty;
        FileName = string.Empty;
    }

    /// <summary>
    /// Gets the markdown content of the file.
    /// </summary>
    public async Task<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return string.Empty;

        return await File.ReadAllTextAsync(FilePath, cancellationToken);
    }

    /// <summary>
    /// Sets the markdown content of the file.
    /// </summary>
    public async Task SetContentAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(FilePath))
            throw new InvalidOperationException("FilePath is not set");

        await File.WriteAllTextAsync(FilePath, content, cancellationToken);

        // Update metadata
        var fileInfo = new FileInfo(FilePath);
        FileSize = fileInfo.Length;
        LastModified = fileInfo.LastWriteTimeUtc;
    }
}

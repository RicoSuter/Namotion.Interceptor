using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage.
/// Content is accessed via methods to avoid tracking overhead.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : ITitleProvider, IIconProvider, IStorageItem
{
    // MudBlazor Icons.Material.Filled.Description
    private const string MarkdownIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M9,13V19H7V13H9M15,15V19H17V15H15M11,11V19H13V11H11Z\" /></svg>";

    private readonly FluentStorageContainer? _storage;
    private string? _cachedTitle;

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
    [State("Size", Order = 1)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Order = 2)]
    public partial DateTime LastModified { get; set; }

    /// <summary>
    /// Gets the title. Returns cached title from front matter or filename.
    /// </summary>
    public string? Title => _cachedTitle ?? Path.GetFileNameWithoutExtension(FileName);

    /// <summary>
    /// Gets the icon for markdown files.
    /// </summary>
    public string Icon => MarkdownIcon;

    public MarkdownFile()
    {
        FilePath = string.Empty;
        FileName = string.Empty;
    }

    // Constructor used by FluentStorageContainer to create files with storage and path
    public MarkdownFile(FluentStorageContainer storage, string filePath)
    {
        _storage = storage;
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    /// <summary>
    /// Sets the cached title (typically called by loader after parsing front matter).
    /// </summary>
    public void SetTitle(string? title)
    {
        _cachedTitle = title;
    }

    /// <summary>
    /// Gets the markdown content of the file.
    /// </summary>
    public async Task<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        if (_storage == null || string.IsNullOrEmpty(FilePath))
            return string.Empty;

        try
        {
            using var stream = await _storage.ReadBlobAsync(FilePath, cancellationToken);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Sets the markdown content of the file.
    /// </summary>
    public async Task SetContentAsync(string content, CancellationToken cancellationToken = default)
    {
        if (_storage == null || string.IsNullOrEmpty(FilePath))
            throw new InvalidOperationException("Storage or FilePath is not set");

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
        stream.Position = 0;

        await _storage.WriteBlobAsync(FilePath, stream, cancellationToken);

        // Update metadata
        FileSize = stream.Length;
        LastModified = DateTime.UtcNow;
    }

    /// <summary>
    /// Extracts navigation title from YAML front matter in markdown content.
    /// Looks for: navigation_title, nav_title, or title (in priority order).
    /// </summary>
    public static string? ExtractTitleFromContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        try
        {
            using var reader = new StringReader(content);
            var firstLine = reader.ReadLine();

            if (firstLine != "---")
                return null;

            // Parse YAML front matter looking for navigation title
            string? navigationTitle = null;
            string? title = null;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line == "---")
                    break;

                if (line.StartsWith("navigation_title:", StringComparison.OrdinalIgnoreCase))
                {
                    navigationTitle = ExtractFrontmatterValue(line, 17);
                }
                else if (line.StartsWith("nav_title:", StringComparison.OrdinalIgnoreCase))
                {
                    navigationTitle ??= ExtractFrontmatterValue(line, 10);
                }
                else if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    title = ExtractFrontmatterValue(line, 6);
                }
            }

            return navigationTitle ?? title;
        }
        catch
        {
            // Ignore parsing errors
            return null;
        }
    }

    private static string? ExtractFrontmatterValue(string line, int prefixLength)
    {
        var value = line.Substring(prefixLength).Trim();
        // Remove quotes if present
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value.Substring(1, value.Length - 2);
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

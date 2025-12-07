using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using MudBlazor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, IDisplaySubject
{
    private string? _cachedTitle;

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    public string? Title => _cachedTitle ?? Path.GetFileNameWithoutExtension(Name);
    public string Icon => Icons.Material.Filled.Article;

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

    public MarkdownFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
    }

    public Task<Stream> ReadAsync(CancellationToken ct = default)
        => Storage.ReadBlobAsync(FullPath, ct);

    public Task WriteAsync(Stream content, CancellationToken ct = default)
        => Storage.WriteBlobAsync(FullPath, content, ct);

    /// <summary>
    /// Sets the cached title (typically called after parsing front matter).
    /// </summary>
    public void SetTitle(string? title)
    {
        _cachedTitle = title;
    }

    /// <summary>
    /// Extracts navigation title from YAML front matter in markdown content.
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
            return null;
        }
    }

    private static string? ExtractFrontmatterValue(string line, int prefixLength)
    {
        var value = line.Substring(prefixLength).Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value.Substring(1, value.Length - 2);
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

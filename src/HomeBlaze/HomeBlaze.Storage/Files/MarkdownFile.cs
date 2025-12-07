using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Pages;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Storage.Internal;
using MudBlazor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPageNavigationProvider
{
    private MarkdownFrontmatter? _frontmatter;
    private bool _frontmatterParsed;
    private string? _cachedContent;

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    public string? Title => GetFrontmatter()?.Title ?? FormatFilename(Name);
    public string? Icon => GetFrontmatter()?.Icon ?? Icons.Material.Filled.Article;
    public string? NavigationTitle => GetFrontmatter()?.NavTitle;
    public int? NavigationOrder => GetFrontmatter()?.Order;

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
    /// Gets the parsed frontmatter, parsing on first access.
    /// </summary>
    private MarkdownFrontmatter? GetFrontmatter()
    {
        if (_frontmatterParsed)
            return _frontmatter;

        _frontmatterParsed = true;

        if (_cachedContent != null)
        {
            _frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(_cachedContent);
        }

        return _frontmatter;
    }

    /// <summary>
    /// Sets the cached content (called after reading file).
    /// </summary>
    public void SetContent(string content)
    {
        _cachedContent = content;
        _frontmatterParsed = false;
        _frontmatter = null;
    }

    private static string FormatFilename(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        return string.Join(" ", baseName
            .Replace("-", " ")
            .Replace("_", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

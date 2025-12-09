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
    private bool _isLoading;

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    public string? Title => GetFrontmatter()?.Title ?? FormatFilename(Name);
    public string? Icon => GetFrontmatter()?.Icon ?? Icons.Material.Filled.Article;
    public string? NavigationTitle => GetFrontmatter()?.NavTitle;
    public int? NavigationOrder => GetFrontmatter()?.Order;

    /// <summary>
    /// Tracked content property - triggers UI refresh on change.
    /// </summary>
    [State]
    public partial string? Content { get; internal set; }

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

    /// <summary>
    /// Partial method hook - triggers lazy loading on first content access.
    /// </summary>
    partial void OnGetContent(ref string? value)
    {
        if (value == null && !_isLoading)
        {
            _isLoading = true;
            _ = LoadContentAsync();
        }
        // Returns current value (null on first access, last known during refresh)
    }

    /// <summary>
    /// Partial method hook - clears cached frontmatter when content changes.
    /// </summary>
    partial void OnSetContent(ref string? value)
    {
        _frontmatter = null;
    }

    private async Task LoadContentAsync()
    {
        try
        {
            await using var stream = await Storage.ReadBlobAsync(FullPath, CancellationToken.None);
            using var reader = new StreamReader(stream);
            Content = await reader.ReadToEndAsync();
            _frontmatter = null;  // Reset to re-parse frontmatter
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Update metadata using storage abstraction
        var metadata = await Storage.GetBlobMetadataAsync(FullPath, cancellationToken);
        if (metadata != null)
        {
            FileSize = metadata.Size;
            LastModified = metadata.LastModifiedUtc ?? DateTime.UtcNow;
        }

        // Reload content (keeps last known until new content loads)
        _isLoading = true;
        try
        {
            await using var stream = await Storage.ReadBlobAsync(FullPath, cancellationToken);
            using var reader = new StreamReader(stream);
            Content = await reader.ReadToEndAsync();
            _frontmatter = null;
        }
        finally
        {
            _isLoading = false;
        }
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken = default)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken = default)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    private MarkdownFrontmatter? GetFrontmatter()
    {
        if (_frontmatter != null || Content == null)
            return _frontmatter;

        _frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(Content);
        return _frontmatter;
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

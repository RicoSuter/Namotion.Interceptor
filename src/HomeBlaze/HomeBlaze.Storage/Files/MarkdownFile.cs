using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage.
/// Content is accessed via methods to avoid tracking overhead.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IPage, IIconProvider, IStorageItem
{
    // MudBlazor Icons.Material.Filled.Description
    private const string MarkdownIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M9,13V19H7V13H9M15,15V19H17V15H15M11,11V19H13V11H11Z\" /></svg>";

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
    /// Gets the navigation title. Extracts from YAML front matter or uses filename.
    /// </summary>
    public string? NavigationTitle => GetNavigationTitleFromMetadata() ?? Path.GetFileNameWithoutExtension(FileName);

    /// <summary>
    /// Gets the icon for markdown files.
    /// </summary>
    public string Icon => MarkdownIcon;

    /// <summary>
    /// Gets the render fragment for displaying markdown content.
    /// </summary>
    public RenderFragment ContentFragment => builder =>
    {
        builder.OpenComponent<MarkdownFileComponent>(0);
        builder.AddAttribute(1, "File", this);
        builder.CloseComponent();
    };

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

    /// <summary>
    /// Extracts navigation title from YAML front matter if present.
    /// Looks for: navigation_title, nav_title, or title (in priority order).
    /// </summary>
    private string? GetNavigationTitleFromMetadata()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return null;

        try
        {
            // Read first few lines to check for front matter
            using var reader = new StreamReader(FilePath);
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
        }

        return null;
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

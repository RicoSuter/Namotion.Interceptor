using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using MarkdownContentParser = HomeBlaze.Storage.Internal.MarkdownContentParser;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage with support for embedded subjects and expressions.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage
{
    private readonly MarkdownContentParser _parser;

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    [Derived]
    public string? Title => Frontmatter?.Title ?? FormatFilename(Name);

    [Derived]
    public string? Icon => Frontmatter?.Icon ?? "Article";

    [Derived]
    public string? NavigationTitle => Frontmatter?.NavTitle;

    [Derived]
    public int? PagePosition => Frontmatter?.Position;

    [Derived]
    public NavigationLocation PageLocation => Frontmatter?.Location ?? NavigationLocation.NavBar;

    [Derived]
    public AppBarAlignment AppBarAlignment => Frontmatter?.Alignment ?? AppBarAlignment.Left;

    public partial MarkdownFrontmatter? Frontmatter { get; private set; }

    /// <summary>
    /// Tracked content property - triggers UI refresh on change.
    /// </summary>
    public partial string? Content { get; private set; }

    /// <summary>
    /// Child subjects parsed from markdown content.
    /// Contains HtmlSegments, RenderExpressions, and embedded subjects.
    /// </summary>
    public partial IDictionary<string, IInterceptorSubject> Children { get; private set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Position = 1)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Position = 2)]
    public partial DateTime LastModified { get; set; }

    public MarkdownFile(IStorageContainer storage, string fullPath, MarkdownContentParser parser)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Children = new Dictionary<string, IInterceptorSubject>();
        _parser = parser;
    }

    private async Task LoadFileAsync(CancellationToken cancellationToken)
    {
        var metadata = await Storage.GetBlobMetadataAsync(FullPath, cancellationToken);
        if (metadata != null)
        {
            FileSize = metadata.Size;
            LastModified = metadata.LastModifiedUtc ?? DateTime.UtcNow;
        }

        await using var stream = await Storage.ReadBlobAsync(FullPath, cancellationToken);
        using var reader = new StreamReader(stream);
        Content = await reader.ReadToEndAsync(cancellationToken);
        Frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(Content);
        Children = await _parser.ParseAsync(Content, this, Children, cancellationToken);
    }

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        return LoadFileAsync(cancellationToken);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

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

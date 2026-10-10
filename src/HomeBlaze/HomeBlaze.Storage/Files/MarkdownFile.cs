using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Parent;
using MarkdownContentParser = HomeBlaze.Storage.Internal.MarkdownContentParser;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage with support for blocks and expressions.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter
{
    private readonly MarkdownContentParser _parser;
    private readonly ConfigurableSubjectSerializer _serializer;

    private MarkdownPage? _page;
    private IReadOnlyDictionary<int, IInterceptorSubject> _blockSubjects = new Dictionary<int, IInterceptorSubject>();
    private IReadOnlyDictionary<string, IInterceptorSubject> _namedSubjects = new Dictionary<string, IInterceptorSubject>();

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    [Derived]
    public string? Title => Frontmatter?.Title ?? FormatFilename(Name);

    public string? IconName => "Article";

    [Derived]
    public string? NavigationTitle => Frontmatter?.NavTitle;

    [Derived]
    public string? NavigationIconName => Frontmatter?.Icon ?? IconName;

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
    /// Contains HtmlSegments, RenderExpressions, and the subjects created by blocks.
    /// </summary>
    [InlinePaths]
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

    public MarkdownFile(
        IStorageContainer storage,
        string fullPath,
        MarkdownContentParser parser,
        ConfigurableSubjectSerializer serializer)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Children = new Dictionary<string, IInterceptorSubject>();
        _parser = parser;
        _serializer = serializer;
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
        var result = await _parser.ParseAsync(Content, this, Children, cancellationToken);
        _page = result.Page;
        _blockSubjects = result.BlockSubjects;
        _namedSubjects = result.NamedSubjects;
        Children = result.Children;
    }

    /// <summary>
    /// Gets the subject that the block with this index created, or null when the block created none.
    /// The index is <see cref="MarkdownBlock.Index"/> of the page as last parsed.
    /// </summary>
    public IInterceptorSubject? GetBlockSubject(int blockIndex) =>
        _blockSubjects.GetValueOrDefault(blockIndex);

    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions", Icon = "Description")]
    public string? ReadRawContent() => Content;

    [Query(Title = "Read Evaluated Content", Description = "Returns the markdown content with {{ path }} expressions evaluated against the current object graph", Icon = "Preview")]
    public string? ReadEvaluatedContent() => _parser.RenderContent(Content, this);

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        return LoadFileAsync(cancellationToken);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        var page = _page;
        if (page is null || FindBlock(subject) is not (var blockIndex, var blockSubject))
        {
            return false;
        }

        // The block spans index into the text the page was parsed from. Content can already be
        // newer than that while a reload is still parsing, so it must not be the text written to.
        var content = MarkdownBlockWriter.WriteConfiguration(
            page.Source, page.AllBlocks[blockIndex], _serializer.Serialize(blockSubject));

        // The spans moved, the block order did not, so the block subjects stay valid.
        _page = MarkdownPageParser.Parse(content);
        Content = content;

        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await WriteAsync(stream, cancellationToken);
        return true;
    }

    private (int BlockIndex, IInterceptorSubject Subject)? FindBlock(IInterceptorSubject subject)
    {
        // The edited subject can be nested in the configuration of a block subject, for example a grid cell.
        for (var current = subject; current is not null && !ReferenceEquals(current, this); current = GetSingleParent(current))
        {
            foreach (var (blockIndex, blockSubject) in _blockSubjects)
            {
                if (ReferenceEquals(blockSubject, current))
                {
                    return (blockIndex, current);
                }
            }
        }

        return null;
    }

    private static IInterceptorSubject? GetSingleParent(IInterceptorSubject subject) =>
        subject.TryGetRegisteredSubject()?.Parents is { Length: 1 } parents ? parents[0].Property.Subject : null;

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

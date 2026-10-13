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
using Namotion.Interceptor.Registry.Attributes;
using MarkdownContentParser = HomeBlaze.Storage.Internal.MarkdownContentParser;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage with support for blocks and expressions.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter, IPathScope, ILinkScope
{
    private readonly MarkdownContentParser _parser;
    private readonly ConfigurableSubjectSerializer _serializer;

    // The parse state is one immutable snapshot that is replaced as a whole. Reloads and write-backs run
    // on different threads, and separate fields would let a reader pair the page of one parse with the
    // subjects of another.
    private volatile MarkdownParseResult _parseResult;

    // A parse changes the subjects it reuses in place, so two reloads at once would leave the nested subjects
    // from one parse and the snapshot from another. Only OnFileChangedAsync takes this. A write-back must not:
    // the storage reloads the file as part of the write, and that reload would wait for the write-back that
    // waits for the write.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // Each access reads the current snapshot. Code that needs two of these together must take
    // _parseResult into a local once and read both from that local.
    private IReadOnlyDictionary<int, IInterceptorSubject> _blockSubjects => _parseResult.BlockSubjects;
    private IReadOnlyDictionary<string, IInterceptorSubject> _namedSubjects => _parseResult.NamedSubjects;

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
    [State("Size", Unit = StateUnit.Byte, Position = 1)]
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
        _parseResult = MarkdownParseResult.Empty;
        Children = _parseResult.Children;
        _parser = parser;
        _serializer = serializer;
    }

    public async Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = await Storage.ReadBlobAsync(FullPath, cancellationToken);
            using var reader = new StreamReader(stream);

            // Limited like a call on the storage: the load runs on the storage worker, which a source that stalls
            // in the middle of the file would otherwise hold.
            var timeProvider = (Storage as FluentStorageContainer)?.TimeProvider ?? TimeProvider.System;
            var reading = reader.ReadToEndAsync(cancellationToken);
            await reading.WithStorageTimeoutAsync("reading", FullPath, timeProvider, cancellationToken);
            var content = await reading;
            var frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(content);
            var result = await _parser.ParseAsync(content, this, Children, cancellationToken);

            // The page itself is assigned only after the parse succeeded, so a failed reload leaves its content,
            // its top-level children and the snapshot as they were. Subjects that the parse reused were already
            // updated in place, with their slots and the children of their fragments.
            //
            // The snapshot must be assigned before Children: the write to Children makes the library compute
            // the derived values again that looked a name up (see TryGetNamedSubject), and they must find
            // the new names then.
            _parseResult = result;
            Content = content;
            Frontmatter = frontmatter;

            // The load runs on the worker of the storage. Without the flow of that work, as for every assignment
            // to the tree: lifecycle handlers run here for a page that is in the tree.
            using (ExecutionContext.SuppressFlow())
            {
                Children = result.Children;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Gets the subject that the block with this index configures, or null when it configures none.
    /// The index is <see cref="MarkdownBlock.Index"/> of the page as last parsed.
    /// </summary>
    /// <remarks>
    /// A slot block that only holds content has no subject here, and neither has a block inside a
    /// block that is shown as an error.
    /// </remarks>
    public IInterceptorSubject? GetBlockSubject(int blockIndex) =>
        _blockSubjects.GetValueOrDefault(blockIndex);

    IInterceptorSubject? IPathScope.TryGetNamedSubject(string name)
    {
        // The name table is not a tracked property, so a derived value that looks a name up would never be
        // computed again when a reload replaces the subject behind the name. Children is tracked and is a
        // new dictionary after every parse, so this read makes such a value depend on the parse. Do not
        // remove it, although its result is not used.
        _ = Children;

        return _namedSubjects.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public string ResolveLink(string link) => _parser.ResolveLink(link, this);

    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions", Icon = "Description")]
    public string? ReadRawContent() => Content;

    [Query(Title = "Read Evaluated Content", Description = "Returns the markdown content with {{ path }} expressions evaluated against the current object graph", Icon = "Preview")]
    public string? ReadEvaluatedContent() => _parser.RenderContent(Content, this);

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        var snapshot = _parseResult;
        if (snapshot.Page is not { } page ||
            FindBlock(snapshot.BlockSubjects, subject) is not (var blockIndex, var blockSubject))
        {
            return false;
        }

        // The block spans index into the text the page was parsed from, which is the source of the
        // page and not necessarily Content.
        var json = _serializer.Serialize(blockSubject, ConfigurationNaming.CSharpProperty);
        var content = MarkdownBlockWriter.WriteConfiguration(page.Source, page.AllBlocks[blockIndex], json);

        // Before the write, because the storage reloads this file from inside WriteAsync. The reload
        // must find the keys that are now in the file, or a value that became null would recreate the subject.
        _parser.RememberConfiguration(blockSubject, json);

        // The spans moved, the block order did not, so the block subjects stay valid.
        _parseResult = snapshot with { Page = MarkdownPageParser.Parse(content) };
        Content = content;

        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await WriteAsync(stream, cancellationToken);
        return true;
    }

    private static (int BlockIndex, IInterceptorSubject Subject)? FindBlock(
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject subject)
    {
        // The edited subject can be nested in the configuration of a block subject, for example a grid cell.
        var blockIndex = -1;
        var blockSubject = subject.TryGetConfigurationOwner(candidate =>
        {
            foreach (var (index, candidateBlockSubject) in blockSubjects)
            {
                if (ReferenceEquals(candidateBlockSubject, candidate))
                {
                    blockIndex = index;
                    return true;
                }
            }

            return false;
        });

        return blockSubject is null ? null : (blockIndex, blockSubject);
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

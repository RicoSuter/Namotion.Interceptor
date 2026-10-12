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
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter, IPathScope
{
    /// <summary>The largest Markdown source, in UTF-8 bytes, that the write operations accept.</summary>
    public const int MaxContentSize = 64 * 1024;

    /// <summary>
    /// The active content, besides blocks, that the write operations cannot add to a file.
    /// </summary>
    public const string ActiveContent =
        "HTML, images and media, math and diagram blocks, element attributes and links other than " +
        "http, https and mailto";

    private readonly MarkdownContentParser _parser;
    private readonly ConfigurableSubjectSerializer _serializer;

    // The parse state is one immutable snapshot that is replaced as a whole. Reloads and write-backs run
    // on different threads, and separate fields would let a reader pair the page of one parse with the
    // subjects of another.
    private volatile MarkdownParseResult _parseResult;

    // A parse changes the subjects it reuses in place, so two reloads at once would leave the nested subjects
    // from one parse and the snapshot from another. Only LoadFileAsync takes this. A write-back must not:
    // the storage reloads the file from inside the write, on the same call chain, and would wait for itself.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // Serializes every write of this file, so the read-modify-write of ReplaceText and of a configuration write-back
    // cannot interleave with another write. It is held while the storage reloads the file after a write, so nothing
    // in that reload may write this file.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Each access reads the current snapshot. Code that needs two of these together must take
    // _parseResult into a local once and read both from that local.
    private IReadOnlyDictionary<int, IInterceptorSubject> _blockSubjects => _parseResult.BlockSubjects;
    private IReadOnlyDictionary<string, IInterceptorSubject> _namedSubjects => _parseResult.NamedSubjects;

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    /// <summary>
    /// Whether the file is in local storage (on disk or in memory), the only storage the write operations change.
    /// </summary>
    public bool IsLocal => MarkdownWriteRules.IsLocal(Storage);

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
        _parseResult = MarkdownParseResult.Empty;
        Children = _parseResult.Children;
        _parser = parser;
        _serializer = serializer;
    }

    private async Task LoadFileAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var metadata = await Storage.GetBlobMetadataAsync(FullPath, cancellationToken);

            await using var stream = await Storage.ReadBlobAsync(FullPath, cancellationToken);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);
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
            Children = result.Children;

            if (metadata != null)
            {
                FileSize = metadata.Size;
                LastModified = metadata.LastModifiedUtc ?? DateTime.UtcNow;
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

    /// <summary>
    /// Returns the Markdown source with unevaluated {{ path }} expressions. Secret configuration values in the blocks
    /// are replaced by "***"; the page editor reads the file itself.
    /// </summary>
    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions. Secret configuration values in blocks are shown as ***", Icon = "Description")]
    public string? ReadRawContent()
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page ? EmbeddedSubjectRedaction.Redact(page, snapshot.BlockSubjects) : Content;
    }

    /// <summary>
    /// Returns the content with each {{ path }} expression replaced by its current value. Secret configuration values
    /// in the blocks are replaced by "***" as in <see cref="ReadRawContent"/>, and an expression of a secret value renders empty.
    /// </summary>
    /// <param name="includeSourcePaths">When true, each resolved value is followed by the absolute path of its
    /// property in parentheses, for example "21.5 (/Devices/Kitchen/Temperature)". Null is treated as false.</param>
    [Query(Title = "Read Evaluated Content", Description = "Returns the markdown content with {{ path }} expressions evaluated against the current object graph. With includeSourcePaths set to true, each value is followed by the absolute path of its property in parentheses, which get_property reads when the property is exposed as state", Icon = "Preview")]
    public string? ReadEvaluatedContent(bool? includeSourcePaths = null) =>
        _parser.RenderContent(ReadRawContent(), this, includeSourcePaths == true);

    /// <summary>
    /// Replaces the whole Markdown source of the file. Its blocks must stay exactly as they are, in the same order, and
    /// the <see cref="ActiveContent"/> may only appear as far as the file already has it.
    /// </summary>
    /// <exception cref="ArgumentException">The content is larger than <see cref="MaxContentSize"/> bytes, adds, changes or
    /// removes a block, or adds such content.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Content", Description = "Replaces the whole markdown source of this file.", Icon = "EditNote")]
    public async Task ReplaceContentAsync(string content, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        var bytes = MarkdownWriteRules.GetContentBytes(content, nameof(content));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            MarkdownWriteRules.EnsureActiveContentUnchanged(await ReadSourceUnlockedAsync(cancellationToken), content, nameof(content));
            await WriteUnlockedAsync(bytes, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/> in the Markdown source of the file with <paramref name="newText"/>,
    /// under the rules of <see cref="ReplaceContentAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, not found, found more than once, the result is larger than
    /// <see cref="MaxContentSize"/> bytes, or it adds, changes or removes a block or adds such content.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Text", Description = "Replaces one exact, unique passage of the markdown source.", Icon = "FindReplace")]
    public async Task ReplaceTextAsync(string oldText, string newText, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("The text to replace must not be empty.", nameof(oldText));
        }

        ArgumentNullException.ThrowIfNull(newText);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var source = await ReadSourceUnlockedAsync(cancellationToken);
            var index = source.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"The text to replace was not found in {Name}. It must match the Markdown source exactly, including whitespace and line breaks.",
                    nameof(oldText));
            }

            // Searching from index + 1 also finds an overlapping second occurrence, which makes the passage ambiguous as well.
            if (source.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException(
                    $"The text to replace occurs more than once in {Name}; include more of the surrounding text so it is unique.",
                    nameof(oldText));
            }

            var updated = string.Concat(source.AsSpan(0, index), newText, source.AsSpan(index + oldText.Length));
            var bytes = MarkdownWriteRules.GetContentBytes(updated, nameof(newText));
            MarkdownWriteRules.EnsureActiveContentUnchanged(source, updated, nameof(newText));
            await WriteUnlockedAsync(bytes, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Reads from storage rather than Content, which lags behind an external edit until its file event arrives.
    private async Task<string> ReadSourceUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = await ReadAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task WriteUnlockedAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await Storage.WriteBlobAsync(FullPath, stream, cancellationToken);
    }

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        return LoadFileAsync(cancellationToken);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public async Task WriteAsync(Stream content, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await Storage.WriteBlobAsync(FullPath, content, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Taken under the lock, so a write-back starts from the page that the previous one wrote.
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

            // Before the write, because the storage reloads this file from inside the write. The reload
            // must find the keys that are now in the file, or a value that became null would recreate the subject.
            _parser.RememberConfiguration(blockSubject, json);

            // The spans moved, the block order did not, so the block subjects stay valid.
            _parseResult = snapshot with { Page = MarkdownPageParser.Parse(content) };
            Content = content;

            await WriteUnlockedAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        return true;
    }

    private (int BlockIndex, IInterceptorSubject Subject)? FindBlock(
        IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject subject)
    {
        // The edited subject can be nested in the configuration of a block subject, for example a grid cell.
        for (var current = subject; current is not null && !ReferenceEquals(current, this); current = GetSingleParent(current))
        {
            foreach (var (blockIndex, blockSubject) in blockSubjects)
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

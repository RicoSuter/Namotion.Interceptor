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
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter, IPathScope
{
    /// <summary>
    /// The largest Markdown source, in UTF-8 bytes, that the write operations accept, measured as the caller writes it,
    /// with secret values hidden; the file can be larger by the hidden values.
    /// </summary>
    public const int MaxContentSize = 64 * 1024;

    /// <summary>
    /// The active content, besides blocks, that the write operations cannot add to a file.
    /// </summary>
    public const string ActiveContent =
        "HTML, images and media, math and diagram blocks, element attributes, abbreviation definitions and links other " +
        "than http, https and mailto";

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
    /// Tracked content property - triggers UI refresh on change. It holds the raw source with the secret values of its
    /// blocks, so page expressions never show it.
    /// </summary>
    [ExcludeFromExpressions]
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

    /// <summary>
    /// Returns the Markdown source with unevaluated {{ path }} expressions. Secret configuration values in the blocks
    /// are replaced by "***"; the page editor reads the file itself. Null while a source that is not empty was not parsed yet.
    /// </summary>
    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions. Secret configuration values in blocks are shown as ***", Icon = "Description")]
    public string? ReadRawContent()
    {
        // Without a parsed page there are no subjects to tell the secret values, so only an empty source is shown.
        var snapshot = _parseResult;
        return snapshot.Page is { } page
            ? EmbeddedSubjectRedaction.Redact(page, snapshot.BlockSubjects)
            : string.IsNullOrEmpty(Content) ? Content : null;
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
    /// Replaces the whole Markdown source of the file as <see cref="ReadRawContent"/> shows it. Its blocks must stay
    /// exactly as that view shows them, with <c>***</c> where it hides a secret value, and keep their secret values. The
    /// <see cref="ActiveContent"/> may only appear as far as the file already has it.
    /// </summary>
    /// <exception cref="ArgumentException">The content is larger than <see cref="MaxContentSize"/> bytes, adds, changes,
    /// removes or moves a block, or adds such content.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Content", Description = "Replaces the whole markdown source of this file.", Icon = "EditNote")]
    public async Task ReplaceContentAsync(string content, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        MarkdownWriteRules.EnsureNotWithinStorageWork(Storage);
        MarkdownWriteRules.GetContentBytes(content, nameof(content));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var source = await ReadSourceUnlockedAsync(cancellationToken);
            var redactedSource = RedactSource(source, out var redactions);
            await WriteUnlockedAsync(MarkdownWriteRules.RestoreBlocks(source, redactedSource, redactions, content, nameof(content)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/> in the Markdown source of the file as
    /// <see cref="ReadRawContent"/> shows it with <paramref name="newText"/>, under the rules of <see cref="ReplaceContentAsync"/>.
    /// An occurrence that overlaps a hidden secret value does not count.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, not found, found more than once, the result is larger than
    /// <see cref="MaxContentSize"/> bytes, or it adds, changes, removes or moves a block or adds such content.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Text", Description = "Replaces one exact, unique passage of the markdown source.", Icon = "FindReplace")]
    public async Task ReplaceTextAsync(string oldText, string newText, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        MarkdownWriteRules.EnsureNotWithinStorageWork(Storage);
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("The text to replace must not be empty.", nameof(oldText));
        }

        ArgumentNullException.ThrowIfNull(newText);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // The passage is searched in the view without secret values, so that its result cannot tell a secret value.
            var source = await ReadSourceUnlockedAsync(cancellationToken);
            var redactedSource = RedactSource(source, out var redactions);
            var index = FindUniquePassage(redactedSource, redactions, oldText);

            var updated = string.Concat(redactedSource.AsSpan(0, index), newText, redactedSource.AsSpan(index + oldText.Length));
            MarkdownWriteRules.GetContentBytes(updated, nameof(newText));
            await WriteUnlockedAsync(MarkdownWriteRules.RestoreBlocks(source, redactedSource, redactions, updated, nameof(newText)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Gets the position of the one occurrence of the passage that overlaps no hidden secret value.</summary>
    private int FindUniquePassage(string redactedSource, List<EmbeddedSubjectRedaction.Redaction> redactions, string oldText)
    {
        var index = -1;
        var count = 0;

        // Searching from the next character also finds an overlapping second occurrence, which makes the passage ambiguous as well.
        for (var match = redactedSource.IndexOf(oldText, StringComparison.Ordinal);
             match >= 0;
             match = redactedSource.IndexOf(oldText, match + 1, StringComparison.Ordinal))
        {
            var end = match + oldText.Length;
            if (!redactions.Exists(redaction => redaction.Redacted.Start < end && match < redaction.Redacted.End))
            {
                index = match;
                count++;
            }
        }

        if (count == 0)
        {
            throw new ArgumentException(
                $"The text to replace was not found in {Name}. It must match the Markdown source as ReadRawContent shows it " +
                "exactly, including whitespace and line breaks, and must not include a hidden secret value (***).",
                "oldText");
        }

        if (count > 1)
        {
            throw new ArgumentException(
                $"The text to replace occurs more than once in {Name}; include more of the surrounding text so it is unique.",
                "oldText");
        }

        return index;
    }

    /// <summary>
    /// Gets the source with its secret values hidden, as <see cref="ReadRawContent"/> shows it when the source is the
    /// one last parsed. A source that was not parsed yet has no subjects to tell the secret values, so every configuration
    /// value of its blocks is hidden.
    /// </summary>
    private string RedactSource(string source, out List<EmbeddedSubjectRedaction.Redaction> redactions)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page && page.Source == source
            ? EmbeddedSubjectRedaction.Redact(page, snapshot.BlockSubjects, out redactions)
            : EmbeddedSubjectRedaction.Redact(MarkdownPageParser.Parse(source), new Dictionary<int, IInterceptorSubject>(), out redactions);
    }

    // Reads from storage rather than Content, which lags behind an external edit until its file event arrives.
    private async Task<string> ReadSourceUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = await ReadAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // Not cancelled once the content is decided: a cancelled reload after the write would leave the parse snapshot of
    // the old content, which a later configuration write-back would write back.
    private async Task WriteUnlockedAsync(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        await Storage.WriteBlobAsync(FullPath, stream, CancellationToken.None);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public async Task WriteAsync(Stream content, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureNotWithinStorageWork(Storage);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Not cancelled once the lock is held, for the reason given at WriteUnlockedAsync.
            await Storage.WriteBlobAsync(FullPath, content, CancellationToken.None);
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
        MarkdownWriteRules.EnsureNotWithinStorageWork(Storage);
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

            await WriteUnlockedAsync(content);
        }
        finally
        {
            _writeLock.Release();
        }

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

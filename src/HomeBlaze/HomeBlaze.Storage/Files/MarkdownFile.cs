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
    // it would write what a running reload applied in place of the change it was called for, and the storage
    // reloads the file as part of the write, which would wait for the write-back that waits for the write.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // A reload changes the subjects it reuses before it publishes its snapshot, and it reuses the values of a
    // slot by their position. Between the two, and after a reload that failed once it had begun, the slot
    // values of the graph do not belong to the blocks of the snapshot, so a write-back must not insert or
    // remove a block then. A reload that publishes while a write-back works makes its exchange fail instead.
    private volatile bool _isGraphAheadOfSnapshot;

    // Serializes every write of this file: the read-modify-write of ReplaceText cannot interleave with another write,
    // and each write-back computes its text from the snapshot that the write before it left, because the reload inside
    // that write has applied the page before the next one reads the graph. A write takes it with WaitAsync after
    // EnsureNotWithinWrite, calls BeginWrite right after, and releases it only through EndWrite. Not the reload lock,
    // which the reload inside the write takes while this one is held.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal const string CallFromWithinWriteMessage =
        "A write of a page was called from within a write of the same page, such as from the " +
        "ApplyConfigurationAsync of a subject that the reload inside that write applies. The call would wait for " +
        "the write that waits for it. " + StorageWorker.CallFromWithinWorkRemedy;

    private const string ReloadRefusal = "Blocks cannot be inserted or removed until the page has been read again.";
    private const string ConfigurationChangedRefusal = "The page changed while the configuration was written; save again.";
    private const string StructureChangedRefusal = "The page changed while it was edited. Try again.";
    private const string NoBlockRefusal = "The subject has no block on the page.";
    private const string TextChangedRefusal = "The text changed since you opened it.";
    private const string PageChangedRefusal = "The page changed since you opened it.";
    private const string NotInContentRefusal = "The block to insert before is not in this content.";

    // The write of this page that the flow runs in, so that a write from inside it is refused instead of waiting
    // for _writeLock. Off the worker, the reload after a write, a refused write-back or a refused structure edit applies
    // configuration within the write.
    private readonly AsyncLocal<WriteOfFlow?> _writeOfFlow = new();

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
            var reading = reader.ReadToEndAsync(cancellationToken);
            await reading.WithStorageTimeoutAsync("reading", FullPath, StorageTimeProvider, cancellationToken);
            var content = await reading;
            var frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

            // A full fence, because a volatile write lets later writes pass it. With the fence before the read of
            // the flag in WriteConfigurationAsync, a write-back that sees a slot value this parse changed also sees
            // the flag.
            _isGraphAheadOfSnapshot = true;
            Thread.MemoryBarrier();

            // A read of the text that the snapshot describes, such as the read inside the write of a structure edit, takes the
            // subjects of the snapshot: the edit has put them where their blocks went, which their keys may not tell. The
            // reload lock keeps any other reload from publishing meanwhile. A write-back that publishes after this read
            // leaves these subjects matched to the text read here, and the next reload, whose text then differs from the
            // snapshot, finds them by their keys again.
            var described = _parseResult;
            var reusableSubjects = described.Page is { } describedPage && describedPage.Source == content
                ? described.BlockSubjects
                : null;
            var result = await _parser.ParseAsync(content, this, Children, reusableSubjects, cancellationToken);

            // The page itself is assigned only after the parse succeeded, so a failed reload leaves its content,
            // its top-level children and the snapshot as they were. Subjects that the parse reused were already
            // updated in place, with their slots and the children of their fragments.
            //
            // The snapshot must be assigned before Children: the write to Children makes the library compute
            // the derived values again that looked a name up (see TryGetNamedSubject), and they must find
            // the new names then.
            _parseResult = result;
            _isGraphAheadOfSnapshot = false;
            Content = content;
            Frontmatter = frontmatter;

            // Without the flow of the caller, which is the worker of the storage or a refused write, as for
            // every assignment to the tree: lifecycle handlers run here for a page that is in the tree.
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
            ? EmbeddedSubjectRedaction.Redact(page, snapshot.BlockSubjects, FindLayoutBlocks(page), out _)
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
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage, such as the
    /// load of a file, or from within a write of this page.</exception>
    [Operation(Title = "Replace Content", Description = "Replaces the whole markdown source of this file.", Icon = "EditNote")]
    public async Task ReplaceContentAsync(string content, CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        MarkdownWriteRules.EnsureContentSize(content, nameof(content));

        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            var source = await ReadSourceUnlockedAsync(cancellationToken);
            var redactedSource = RedactSource(source, out var redactions);
            await WriteUnlockedAsync(MarkdownWriteRules.RestoreBlocks(source, redactedSource, redactions, content, nameof(content), GetPageLayoutRules()));
        }
        finally
        {
            EndWrite(write);
        }
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/> in the Markdown source of the file as
    /// <see cref="ReadRawContent"/> shows it with <paramref name="newText"/>, under the rules of <see cref="ReplaceContentAsync"/>.
    /// An occurrence that overlaps a hidden secret value does not count.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, not found, found more than once, the result is larger than
    /// <see cref="MaxContentSize"/> bytes, or it adds, changes, removes or moves a block or adds such content.</exception>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage, such as the
    /// load of a file, or from within a write of this page.</exception>
    [Operation(Title = "Replace Text", Description = "Replaces one exact, unique passage of the markdown source.", Icon = "FindReplace")]
    public async Task ReplaceTextAsync(string oldText, string newText, CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("The text to replace must not be empty.", nameof(oldText));
        }

        ArgumentNullException.ThrowIfNull(newText);

        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            // The passage is searched in the view without secret values, so that its result cannot tell a secret value.
            var source = await ReadSourceUnlockedAsync(cancellationToken);
            var redactedSource = RedactSource(source, out var redactions);
            var index = FindUniquePassage(redactedSource, redactions, oldText);

            var updated = string.Concat(redactedSource.AsSpan(0, index), newText, redactedSource.AsSpan(index + oldText.Length));
            MarkdownWriteRules.EnsureContentSize(updated, nameof(newText));
            await WriteUnlockedAsync(MarkdownWriteRules.RestoreBlocks(source, redactedSource, redactions, updated, nameof(newText), GetPageLayoutRules()));
        }
        finally
        {
            EndWrite(write);
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
        if (snapshot.Page is { } page && page.Source == source)
        {
            return EmbeddedSubjectRedaction.Redact(page, snapshot.BlockSubjects, FindLayoutBlocks(page), out redactions);
        }

        var unparsedPage = MarkdownPageParser.Parse(source);
        return EmbeddedSubjectRedaction.Redact(
            unparsedPage, new Dictionary<int, IInterceptorSubject>(), FindLayoutBlocks(unparsedPage), out redactions);
    }

    private HashSet<MarkdownBlock> FindLayoutBlocks(MarkdownPage page) => MarkdownPageLayout.FindLayoutBlocks(page, _parser.FindType);

    // The rules of the permission to edit page layout when the caller has it, which a reference path resolves from this page.
    private PageLayoutRules? GetPageLayoutRules() => MarkdownPageLayoutPermission.IsGranted
        ? new PageLayoutRules(_parser.FindType, path => _parser.ResolvesToSubject(path, this), _parser.FindLayoutError)
        : null;

    private TimeProvider StorageTimeProvider => (Storage as FluentStorageContainer)?.TimeProvider ?? TimeProvider.System;

    // Reads from storage rather than Content, which lags behind an external edit until its file event arrives. Limited
    // like a call on the storage, because the caller holds _writeLock, which a stalled read would otherwise keep.
    private async Task<string> ReadSourceUnlockedAsync(CancellationToken cancellationToken)
    {
        // The client returns null for a file that is gone, although its signature does not say so.
        await using var stream = await ReadAsync(cancellationToken)
            ?? throw new FileNotFoundException($"'{FullPath}' is not in the storage.", FullPath);
        using var reader = new StreamReader(stream);
        var reading = reader.ReadToEndAsync(cancellationToken);
        await reading.WithStorageTimeoutAsync("reading", FullPath, StorageTimeProvider, cancellationToken);
        return await reading;
    }

    // Not cancelled once the content is decided: a cancelled reload after the write would leave the parse snapshot of
    // the old content, which a later configuration write-back would write back.
    private Task WriteUnlockedAsync(string content) =>
        Storage.WriteBlobAsync(FullPath, Encoding.UTF8.GetBytes(content), CancellationToken.None);

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage, such as the
    /// load of a file, or from within a write of this page.</exception>
    public async Task WriteAsync(Stream content, CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            // Not cancelled once the lock is held, for the reason given at WriteUnlockedAsync.
            await Storage.WriteBlobAsync(FullPath, content, CancellationToken.None);
        }
        finally
        {
            EndWrite(write);
        }
    }

    /// <summary>
    /// Writes the Markdown source to the file as it is, once no other write of the file is under way. The storage then
    /// refreshes the page as best effort: a failed refresh is logged, and the next pass reloads the file. The write is
    /// not cancelled once it has begun.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage, such as the
    /// load of a file, or from within a write of this page.</exception>
    public async Task WriteAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureNotWithinWrite();
        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            await WriteUnlockedAsync(content);
        }
        finally
        {
            EndWrite(write);
        }
    }

    // From work on the worker or within a write of this page, such as the configuration that the reload inside a
    // write applies, the call would wait for the lock that the write holds while it waits for that work. The worker
    // would refuse it later.
    private void EnsureNotWithinWrite()
    {
        MarkdownWriteRules.EnsureNotWithinStorageWork(Storage);
        if (_writeOfFlow.Value is { IsFinished: false })
        {
            throw new InvalidOperationException(CallFromWithinWriteMessage);
        }
    }

    // Not async, so that the value it sets stays in the flow of the caller, which holds _writeLock.
    private WriteOfFlow BeginWrite()
    {
        var write = new WriteOfFlow();
        _writeOfFlow.Value = write;
        return write;
    }

    private void EndWrite(WriteOfFlow write)
    {
        // A flow that the write started and did not await keeps the value, and may write once the write is over.
        write.IsFinished = true;
        _writeLock.Release();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the configuration of the block that contains the subject. For every slot of that block subject that
    /// is a list of subjects of a type, a value without a block gets a new block and a block whose value is gone
    /// is removed with its content. Returns false, and writes nothing, when the page has no block for the subject,
    /// when the page cannot keep the change or the change cannot be made without touching other content, when
    /// blocks would be inserted or removed while a reload runs or after one that failed, until the page has been
    /// read again, or when a reload changed the page while this one ran. When the page cannot keep the change, it is
    /// read again from its file before this returns, so the graph matches the text again unless that read fails.
    /// When the storage fails the write, the page is read again in the same way and the exception propagates.
    /// Saves of one page run one after the other.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage, such as the
    /// load of a file, or from within a write of this page.</exception>
    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            return await WriteBlockAsync(subject, cancellationToken);
        }
        finally
        {
            EndWrite(write);
        }
    }

    /// <summary>Deletes the block of <paramref name="subject"/> with everything in it and saves the page.</summary>
    /// <remarks>
    /// Refused when the file differs from the text the page last read, and the page then reads it, or when the page would
    /// be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> DeleteBlockAsync(IInterceptorSubject subject, CancellationToken cancellationToken) =>
        EditStructureAsync(
            snapshot => FindOwnBlock(snapshot.Page!, snapshot.BlockSubjects, subject) is { } block
                ? (MarkdownPageOperations.TryRemove(snapshot.Page!, snapshot.BlockSubjects, block, out var refusal), refusal)
                : (null, NoBlockRefusal),
            StructureChangedRefusal,
            cancellationToken);

    /// <summary>
    /// Moves the block of <paramref name="subject"/> one place up (-1) or down (1) among the content around it: before the
    /// item before it, or after the item after it, where an item is a widget block or a top-level block of text of that
    /// content, or for a slot block a block of the same slot. Saves the page.
    /// </summary>
    /// <remarks>
    /// Refused when the file differs from the text the page last read, and the page then reads it, or when the page would
    /// be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> MoveBlockAsync(
        IInterceptorSubject subject, int direction, CancellationToken cancellationToken) =>
        EditStructureAsync(
            snapshot => FindOwnBlock(snapshot.Page!, snapshot.BlockSubjects, subject) is { } block
                ? (MarkdownPageOperations.TryMove(snapshot.Page!, snapshot.BlockSubjects, block, direction, out var refusal), refusal)
                : (null, NoBlockRefusal),
            StructureChangedRefusal,
            cancellationToken);

    /// <summary>
    /// Inserts a block for <paramref name="widget"/>, with its configuration and <paramref name="name"/> after <c>#</c>,
    /// into <paramref name="content"/>: this page or a fragment of one of its blocks. It goes before <paramref name="before"/>,
    /// a block of that content or the <see cref="HtmlSegment"/> of one of its text blocks, or after the content when that is
    /// null. The page then creates its own subject for it.
    /// </summary>
    /// <remarks>
    /// Refused when the file differs from the text the page last read, and the page then reads it, or when the page would
    /// be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> InsertWidgetAsync(
        IInterceptorSubject content, IInterceptorSubject? before, IInterceptorSubject widget, string? name, CancellationToken cancellationToken)
    {
        var typeName = widget.GetType().FullName!;
        var configurationJson = MarkdownBlockConfiguration.Serialize(_serializer, widget, block: null);
        return EditStructureAsync(
            snapshot => TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out var refusal)
                ? (MarkdownPageOperations.TryInsertWidget(
                    snapshot.Page!, snapshot.BlockSubjects, owner, line, typeName, name, configurationJson, out refusal), refusal)
                : (null, refusal),
            GetChangedPageRefusal(before),
            cancellationToken);
    }

    /// <summary>
    /// Gets the Markdown of the text block that <paramref name="segment"/> starts, without the indentation of its content and
    /// with line feeds, or null when the segment starts no text block that can be edited.
    /// </summary>
    public string? GetTextBlockText(HtmlSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var snapshot = _parseResult;
        return snapshot.Page is { } page && snapshot.TextBlocks.TryGetValue(segment, out var textBlock) && textBlock.IsEditable
            ? MarkdownPageOperations.GetText(page, textBlock)
            : null;
    }

    /// <summary>
    /// Replaces the text block that <paramref name="segment"/> starts with <paramref name="newText"/>, or removes it when that
    /// is empty or white space, and saves the page. Complete widget blocks at the top level of the new text become blocks of
    /// the content, and the page creates their subjects.
    /// </summary>
    /// <remarks>
    /// Refused when the segment starts no text block that can be edited any more or its text is no longer
    /// <paramref name="expectedText"/>, as <see cref="GetTextBlockText"/> returned it, when the new text holds a block that is
    /// no widget block at its top level, a widget block with fewer fence characters than
    /// <see cref="GetWidgetFenceLengthInText"/>, an invalid block, or a widget block whose name a block of the page or another
    /// block of the text already has, when the file differs from the text the page last read, and the page then reads it, or
    /// when the page would be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> ReplaceTextBlockAsync(
        HtmlSegment segment, string expectedText, string newText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(expectedText);
        ArgumentNullException.ThrowIfNull(newText);
        var expected = expectedText.ReplaceLineEndings("\n");
        return EditStructureAsync(
            snapshot =>
            {
                if (!snapshot.TextBlocks.TryGetValue(segment, out var textBlock) || !textBlock.IsEditable ||
                    MarkdownPageOperations.GetText(snapshot.Page!, textBlock) != expected)
                {
                    return (null, TextChangedRefusal);
                }

                return (MarkdownPageOperations.TryReplaceText(snapshot.Page!, snapshot.BlockSubjects, textBlock, newText, out var refusal), refusal);
            },
            TextChangedRefusal,
            cancellationToken);
    }

    /// <summary>
    /// Inserts <paramref name="text"/> into <paramref name="content"/> before <paramref name="before"/>, as
    /// <see cref="InsertWidgetAsync"/> places a widget, and saves the page. Complete widget blocks at the top level of the text
    /// become blocks of the content, and the page creates their subjects.
    /// </summary>
    /// <remarks>
    /// Refused when the text holds a block that is no widget block at its top level, a widget block with fewer fence
    /// characters than <see cref="GetWidgetFenceLengthInContent"/>, an invalid block, or a widget block whose name a block of
    /// the page or another block of the text already has, when the file differs from the text the page last read, and the
    /// page then reads it, or when the page would be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> InsertTextAsync(
        IInterceptorSubject content, IInterceptorSubject? before, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EditStructureAsync(
            snapshot => TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out var refusal)
                ? (MarkdownPageOperations.TryInsertText(snapshot.Page!, snapshot.BlockSubjects, owner, line, text, out refusal), refusal)
                : (null, refusal),
            GetChangedPageRefusal(before),
            cancellationToken);
    }

    /// <summary>
    /// Moves the text block that <paramref name="segment"/> starts one place up (-1) or down (1) among the content around it,
    /// as <see cref="MoveBlockAsync"/> moves a block, and saves the page.
    /// </summary>
    /// <remarks>
    /// Refused when the segment starts no text block that can be edited any more, when there is nothing to move past in the
    /// direction, when the file differs from the text the page last read, and the page then reads it, or when the page would
    /// be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> MoveTextBlockAsync(HtmlSegment segment, int direction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return EditStructureAsync(
            snapshot => snapshot.TextBlocks.TryGetValue(segment, out var textBlock)
                ? (MarkdownPageOperations.TryMoveText(snapshot.Page!, snapshot.BlockSubjects, textBlock, direction, out var refusal), refusal)
                : (null, TextChangedRefusal),
            TextChangedRefusal,
            cancellationToken);
    }

    /// <summary>
    /// Gets whether text can go into <paramref name="content"/> before <paramref name="before"/>, or at its end: not right after
    /// a block without a closing line, which would take the text.
    /// </summary>
    public bool CanInsertTextAt(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page &&
               TryGetInsertPoint(snapshot, content, before, out var owner, out var line, out _) &&
               MarkdownPageOperations.CanInsertTextAt(page, owner, line);
    }

    /// <summary>
    /// Gets how many fence characters a widget block needs at least to stand directly in the content of the text block that
    /// <paramref name="segment"/> starts: two on the page, one more than the block whose content it is otherwise. Zero when
    /// the segment starts no text block of this page.
    /// </summary>
    public int GetWidgetFenceLengthInText(HtmlSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var snapshot = _parseResult;
        return snapshot.TextBlocks.TryGetValue(segment, out var textBlock)
            ? MarkdownPageOperations.GetWidgetFenceLength(textBlock.Owner)
            : 0;
    }

    /// <summary>
    /// Gets how many fence characters a widget block needs at least to stand directly in <paramref name="content"/>, this
    /// page or the content of one of its blocks: two on the page, one more than the block whose content it is otherwise. Zero
    /// when the content is not on this page.
    /// </summary>
    public int GetWidgetFenceLengthInContent(IInterceptorSubject content)
    {
        if (ReferenceEquals(content, this))
        {
            return MarkdownPageOperations.GetWidgetFenceLength(null);
        }

        var snapshot = _parseResult;
        return snapshot.Page is { } page && MarkdownPageOperations.FindContentBlock(page, snapshot.BlockSubjects, content) is { } owner
            ? MarkdownPageOperations.GetWidgetFenceLength(owner)
            : 0;
    }

    // The block whose content `content` is, or null for the page, and the line before which new content goes: the first line
    // of `before`, a block or a text block of that content, or the end of the content.
    private bool TryGetInsertPoint(
        MarkdownParseResult snapshot,
        IInterceptorSubject content,
        IInterceptorSubject? before,
        out MarkdownBlock? owner,
        out int line,
        out string? refusal)
    {
        var page = snapshot.Page!;
        owner = null;
        line = 0;
        if (!ReferenceEquals(content, this) &&
            (owner = MarkdownPageOperations.FindContentBlock(page, snapshot.BlockSubjects, content)) is null)
        {
            refusal = "The content is not on this page.";
            return false;
        }

        int? beforeLine = null;
        if (before is HtmlSegment segment)
        {
            if (!snapshot.TextBlocks.TryGetValue(segment, out var textBlock))
            {
                refusal = PageChangedRefusal;
                return false;
            }

            if (!ReferenceEquals(textBlock.Owner, owner))
            {
                refusal = NotInContentRefusal;
                return false;
            }

            beforeLine = textBlock.Line;
        }
        else if (before is not null)
        {
            if (FindOwnBlock(page, snapshot.BlockSubjects, before) is not { } beforeBlock)
            {
                refusal = NoBlockRefusal;
                return false;
            }

            MarkdownPageOperations.FindSiblings(page, beforeBlock, out var parent);
            if (!ReferenceEquals(parent, owner))
            {
                refusal = NotInContentRefusal;
                return false;
            }

            beforeLine = beforeBlock.Line;
        }

        line = MarkdownPageOperations.GetInsertLine(page, owner, beforeLine);
        refusal = null;
        return true;
    }

    // A segment of page text survives the read after a refusal only while nothing before it changed, so a refusal that
    // asks to try again would often fail again.
    private static string GetChangedPageRefusal(IInterceptorSubject? before) =>
        before is HtmlSegment ? PageChangedRefusal : StructureChangedRefusal;

    /// <summary>
    /// Appends a block to the list slot <paramref name="slotName"/> of the block of <paramref name="owner"/>: with the
    /// configuration of <paramref name="item"/>, or empty for a slot whose blocks hold page content (null item).
    /// </summary>
    /// <remarks>
    /// Refused when the file differs from the text the page last read, and the page then reads it, or when the page would
    /// be larger than <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> AddSlotItemAsync(
        IInterceptorSubject owner, string slotName, IInterceptorSubject? item, CancellationToken cancellationToken)
    {
        var configurationJson = item is null ? "{}" : MarkdownBlockConfiguration.Serialize(_serializer, item, block: null);
        return EditStructureAsync(
            snapshot =>
            {
                var page = snapshot.Page!;
                var subjects = snapshot.BlockSubjects;
                if (FindOwnBlock(page, subjects, owner) is not { } block)
                {
                    return (null, NoBlockRefusal);
                }

                if (!SlotMap.For(owner.GetType()).Named.TryGetValue(slotName, out var slot) || !slot.IsList ||
                    (item is null ? !slot.IsOpen : item.GetType() != slot.ValueType))
                {
                    return (null, $"The block has no list slot '{slotName}' for this item.");
                }

                return (MarkdownPageOperations.TryAppendSlotBlock(page, subjects, block, slotName, configurationJson, out var refusal), refusal);
            },
            StructureChangedRefusal,
            cancellationToken);
    }

    /// <summary>
    /// Adds a block for <paramref name="widget"/>, with its configuration and <paramref name="name"/> after <c>#</c>, at the
    /// end of the content of the slot <paramref name="slotName"/> of the block of <paramref name="owner"/>, or of its default
    /// slot when that is null, and saves the page. The content of the default slot ends before the first slot block of the
    /// block. A named slot without a block gets one, and a list slot always gets a new block. The page then creates the
    /// subject of the widget.
    /// </summary>
    /// <remarks>
    /// Refused when the block has no slot of that name that takes page content or no closing line, when the file differs
    /// from the text the page last read, and the page then reads it, or when the page would be larger than
    /// <see cref="MaxContentSize"/> bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> AddWidgetToSlotAsync(
        IInterceptorSubject owner, string? slotName, IInterceptorSubject widget, string? name, CancellationToken cancellationToken)
    {
        var typeName = widget.GetType().FullName!;
        var configurationJson = MarkdownBlockConfiguration.Serialize(_serializer, widget, block: null);
        return AddToSlotAsync(
            owner,
            slotName,
            (MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> subjects, MarkdownBlock container, int line, out string? refusal) =>
                MarkdownPageOperations.TryInsertWidget(page, subjects, container, line, typeName, name, configurationJson, out refusal),
            cancellationToken);
    }

    /// <summary>
    /// Adds <paramref name="text"/> at the end of the content of the slot <paramref name="slotName"/> of the block of
    /// <paramref name="owner"/>, or of its default slot when that is null, where <see cref="AddWidgetToSlotAsync"/> adds a
    /// widget, and saves the page. Complete widget blocks at the top level of the text become blocks of the content, as with
    /// <see cref="InsertTextAsync"/>, and the page creates their subjects.
    /// </summary>
    /// <remarks>
    /// Refused as <see cref="AddWidgetToSlotAsync"/> and <see cref="InsertTextAsync"/> are.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage or from within a write of this page.</exception>
    public Task<MarkdownEditResult> AddTextToSlotAsync(
        IInterceptorSubject owner, string? slotName, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return AddToSlotAsync(
            owner,
            slotName,
            (MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> subjects, MarkdownBlock container, int line, out string? refusal) =>
                MarkdownPageOperations.TryInsertText(page, subjects, container, line, text, out refusal),
            cancellationToken);
    }

    private Task<MarkdownEditResult> AddToSlotAsync(
        IInterceptorSubject owner, string? slotName, MarkdownContentInsert insert, CancellationToken cancellationToken) =>
        EditStructureAsync(
            snapshot =>
            {
                var page = snapshot.Page!;
                if (FindOwnBlock(page, snapshot.BlockSubjects, owner) is not { } block)
                {
                    return (null, NoBlockRefusal);
                }

                var slots = SlotMap.For(owner.GetType());
                if (FindContentSlot(slots, slotName) is not { } slot)
                {
                    return (null, slotName is null
                        ? "The block has no default slot for content."
                        : $"The block has no slot '{slotName}' for content.");
                }

                return (MarkdownPageOperations.TryAddToSlot(page, snapshot.BlockSubjects, block, slots, slot, insert, out var refusal), refusal);
            },
            StructureChangedRefusal,
            cancellationToken);

    // The default slot when the name is null, and only a slot that takes page content: a slot of configured items takes none.
    private static SlotDefinition? FindContentSlot(SlotMap slots, string? slotName)
    {
        var slot = slotName is null ? slots.Default : slots.Named.GetValueOrDefault(slotName);
        return slot is { IsOpen: true } ? slot : null;
    }

    /// <summary>
    /// Gets the slots of the block of <paramref name="owner"/> that take page content, in the order the type declares them,
    /// for <see cref="AddWidgetToSlotAsync"/> and <see cref="AddTextToSlotAsync"/>. None when it has no block on this page or
    /// its block has no closing line. <see cref="MarkdownContentSlot.Exists"/> tells whether the slot has a content subject
    /// now; an add to a slot of <see cref="MarkdownContentSlotKind.ListItem"/> creates a new item whether or not it has any.
    /// It reads the children of the page, so a component that renders it renders again after every reload.
    /// </summary>
    public IReadOnlyList<MarkdownContentSlot> GetContentSlots(IInterceptorSubject owner)
    {
        // The snapshot is not tracked, as in GetBlockActions. Do not remove it, although its result is not used.
        _ = Children;

        var snapshot = _parseResult;
        if (snapshot.Page is not { } page || FindOwnBlock(page, snapshot.BlockSubjects, owner) is not { HasClosingLine: true })
        {
            return [];
        }

        var result = new List<MarkdownContentSlot>();
        foreach (var slot in SlotMap.For(owner.GetType()).Slots)
        {
            if (!slot.IsOpen)
            {
                continue;
            }

            var kind = slot.IsList ? MarkdownContentSlotKind.ListItem
                : slot.IsDefault ? MarkdownContentSlotKind.Default
                : MarkdownContentSlotKind.Named;
            var value = slot.GetValue(owner);
            var exists = value is Array values ? values.Length > 0 : value is not null;
            result.Add(new MarkdownContentSlot(slot.IsDefault ? null : slot.Name, kind, exists));
        }

        return result;
    }

    /// <summary>
    /// Gets what the block of <paramref name="subject"/>, or the text block that a <see cref="HtmlSegment"/> starts, allows:
    /// all false when it has no block on this page, or the text block cannot be edited. It reads the children of the page, so
    /// a component that renders it renders again after every reload.
    /// </summary>
    public MarkdownBlockActions GetBlockActions(IInterceptorSubject subject)
    {
        // The snapshot is not tracked, and the bar of a selected widget that a move kept would show the buttons of its old
        // place. Do not remove it, although its result is not used.
        _ = Children;

        var snapshot = _parseResult;
        if (snapshot.Page is not { } page)
        {
            return default;
        }

        if (subject is HtmlSegment segment)
        {
            if (!snapshot.TextBlocks.TryGetValue(segment, out var textBlock) || !textBlock.IsEditable)
            {
                return default;
            }

            var (canMoveTextUp, canMoveTextDown) = MarkdownPageOperations.GetTextMoveDirections(page, textBlock);
            return new MarkdownBlockActions(CanDelete: true, CanMoveUp: canMoveTextUp, CanMoveDown: canMoveTextDown, ContainedBlockCount: 0);
        }

        if (FindOwnBlock(page, snapshot.BlockSubjects, subject) is not { } block)
        {
            return default;
        }

        var (canMoveUp, canMoveDown) = MarkdownPageOperations.GetMoveDirections(page, block);
        return new MarkdownBlockActions(
            CanDelete: true,
            CanMoveUp: canMoveUp,
            CanMoveDown: canMoveDown,
            ContainedBlockCount: MarkdownPageOperations.CountBlocks(block) - 1);
    }

    /// <summary>Gets the list slots of the block of <paramref name="owner"/>, none when it has no block on this page.</summary>
    public IReadOnlyList<MarkdownListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var snapshot = _parseResult;
        if (snapshot.Page is not { } page || FindOwnBlock(page, snapshot.BlockSubjects, owner) is null)
        {
            return [];
        }

        var result = new List<MarkdownListSlot>();
        foreach (var slot in SlotMap.For(owner.GetType()).Slots)
        {
            if (slot.IsList)
            {
                result.Add(new MarkdownListSlot(slot.Name, slot.IsOpen ? null : slot.ValueType));
            }
        }

        return result;
    }

    /// <summary>Gets the name after <c>#</c> of the block of <paramref name="subject"/>, or null without one.</summary>
    public string? GetBlockName(IInterceptorSubject subject)
    {
        var snapshot = _parseResult;
        return snapshot.Page is { } page ? FindOwnBlock(page, snapshot.BlockSubjects, subject)?.Head?.Id : null;
    }

    /// <summary>Gets whether <paramref name="content"/> is this page or the content of one of its blocks.</summary>
    public bool IsContentOfPage(IInterceptorSubject content)
    {
        var snapshot = _parseResult;
        return ReferenceEquals(content, this) ||
               (snapshot.Page is { } page && MarkdownPageOperations.FindContentBlock(page, snapshot.BlockSubjects, content) is not null);
    }

    // changedPageRefusal is the refusal when the file or the snapshot changed meanwhile.
    private async Task<MarkdownEditResult> EditStructureAsync(
        Func<MarkdownParseResult, (MarkdownStructureChange? Change, string? Refusal)> operation,
        string changedPageRefusal,
        CancellationToken cancellationToken)
    {
        EnsureNotWithinWrite();
        await _writeLock.WaitAsync(cancellationToken);
        var write = BeginWrite();
        try
        {
            var snapshot = _parseResult;
            if (snapshot.Page is not { } page)
            {
                return MarkdownEditResult.Refused("The page has not been read yet.");
            }

            // The new text is computed from the snapshot, which lags behind a change of the file until its event arrives,
            // and would undo that change. The page is read now, so that a second try works on the file.
            if (await ReadSourceUnlockedAsync(cancellationToken) != page.Source)
            {
                _parser.LogRefusedWrite(FullPath, changedPageRefusal);
                await ReloadAfterRefusalAsync(cancellationToken);
                return MarkdownEditResult.Refused(changedPageRefusal);
            }

            // The operations change only text, so a refusal leaves the graph as the snapshot has it.
            var (change, refusal) = operation(snapshot);
            if (change is null)
            {
                _parser.LogRefusedWrite(FullPath, refusal!);
                return MarkdownEditResult.Refused(refusal!);
            }

            if (MarkdownWriteRules.GetContentGrowthRefusal(change.Write.Page.Source, page.Source) is { } sizeRefusal)
            {
                _parser.LogRefusedWrite(FullPath, sizeRefusal);
                return MarkdownEditResult.Refused(sizeRefusal);
            }

            var publishRefusal = await PublishAndWriteAsync(snapshot, change.Write, change.ChangedBlock, changedPageRefusal);
            return publishRefusal is null
                ? MarkdownEditResult.WrittenFor(FindResultSubject(change))
                : MarkdownEditResult.Refused(publishRefusal);
        }
        finally
        {
            EndWrite(write);
        }
    }

    // After the write: the read inside it has created the subject of a new block and the segments of the text, when the
    // storage read the file there. Without that read, the published snapshot still names a moved subject, because the edit
    // carried it, but has no text blocks. A new block without a subject, such as one of an unknown type, falls back to the
    // text that the change wrote.
    private IInterceptorSubject? FindResultSubject(MarkdownStructureChange change)
    {
        var snapshot = _parseResult;
        if (snapshot.Page?.Source != change.Write.Page.Source)
        {
            return null;
        }

        if (change.ResultBlockIndex >= 0 && snapshot.BlockSubjects.GetValueOrDefault(change.ResultBlockIndex) is { } blockSubject)
        {
            return blockSubject;
        }

        if (change.ResultTextLine > 0)
        {
            foreach (var (segment, textBlock) in snapshot.TextBlocks)
            {
                if (textBlock.Line == change.ResultTextLine)
                {
                    return segment;
                }
            }
        }

        return null;
    }

    // The block whose own subject the subject is. Unlike FindBlock, not the block that holds the subject in its
    // configuration, such as the grid of a cell, which a delete must never take.
    private static MarkdownBlock? FindOwnBlock(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject subject)
    {
        foreach (var (index, candidate) in blockSubjects)
        {
            if (ReferenceEquals(candidate, subject))
            {
                return page.AllBlocks[index];
            }
        }

        return null;
    }

    // Under _writeLock: no other write of this file runs, so only a reload can change the snapshot meanwhile.
    private async Task<bool> WriteBlockAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var snapshot = _parseResult;
        var blockIndex = FindBlock(snapshot.BlockSubjects, subject);
        if (snapshot.Page is not { } page || blockIndex < 0)
        {
            return false;
        }

        // The block spans index into the text the page was parsed from, which is the source of the
        // page and not necessarily Content.
        var write = MarkdownBlockReconciler.TryWrite(page, snapshot.BlockSubjects, blockIndex, _serializer, out var refusal);
        if (write is null)
        {
            _parser.LogRefusedWrite(FullPath, refusal!);

            // The graph keeps the refused values of a slot, and a later reload would give the blocks to them by
            // their position, so a value would take the block of another one. The reload keeps the blocks whose
            // text did not change as they are in memory, so this block and the blocks inside it are made to take
            // their text again, and the blocks around it are read in full so that the reload reaches them.
            ForgetAppliedConfigurations(page.AllBlocks[blockIndex], snapshot.BlockSubjects);
            ForgetAppliedBlocksAround(page, page.AllBlocks[blockIndex], snapshot.BlockSubjects);
            await ReloadAfterRefusalAsync(cancellationToken);
            return false;
        }

        return await PublishAndWriteAsync(snapshot, write, page.AllBlocks[blockIndex], ConfigurationChangedRefusal) is null;
    }

    // Under _writeLock, after a refusal. A failed read is logged, and the next pass of the storage reads the file again.
    private async Task ReloadAfterRefusalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await OnFileChangedAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _parser.LogFailedReload(FullPath, exception);
        }
    }

    // Under _writeLock: publishes the write as the snapshot that follows `snapshot` and writes its text. Returns why
    // nothing was written, or null. `changedBlock` is the block of the snapshot whose text changed, null for the page.
    private async Task<string?> PublishAndWriteAsync(
        MarkdownParseResult snapshot, MarkdownBlockWrite write, MarkdownBlock? changedBlock, string changedPageRefusal)
    {
        // After the graph was read and before anything is published: the blocks were compared with slot values
        // that a reload may have changed since the snapshot was parsed. A reload that published since then fails
        // the exchange below. A full fence, because a volatile read lets the earlier reads of the graph pass it
        // (see OnFileChangedAsync).
        Thread.MemoryBarrier();
        if (write.HasInsertedOrRemovedBlocks && _isGraphAheadOfSnapshot)
        {
            _parser.LogRefusedWrite(FullPath, ReloadRefusal);
            return ReloadRefusal;
        }

        // The text was computed from the snapshot, so it would undo what a reload published since then.
        // The text blocks have the lines of the text before the write; the read inside the write finds the new ones.
        var published = snapshot with
        {
            Page = write.Page,
            BlockSubjects = write.BlockSubjects,
            TextBlocks = MarkdownParseResult.NoTextBlocks
        };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _parseResult, published, snapshot), snapshot))
        {
            _parser.LogRefusedWrite(FullPath, changedPageRefusal);
            return changedPageRefusal;
        }

        // The text of the block and of the blocks around it changes in the file, so a later read of the text that
        // they had before is a change that must be applied, also when the read inside the write is skipped or fails.
        // The blocks inside it keep their text.
        if (changedBlock is not null)
        {
            ForgetAppliedBlocksAround(snapshot.Page!, changedBlock, snapshot.BlockSubjects);
        }

        // Before the write, because the storage reloads this file from inside the write. The reload
        // must find the keys that are now in the file, or a value that became null would recreate the subject.
        foreach (var (writtenSubject, configurationJson) in write.Configurations)
        {
            _parser.RememberConfiguration(writtenSubject, configurationJson);
        }

        var content = write.Page.Source;
        var previousContent = Content;
        Content = content;

        try
        {
            await WriteUnlockedAsync(content);
        }
        catch
        {
            // Published ahead of the file, the next write would be computed from this one and save it after all. The
            // snapshot goes back only while no reload replaced it, and the read then takes in whatever the file holds,
            // also the text of a write that gave up late. It applies the blocks that were forgotten above again, and
            // the configurations of this write, which the graph holds but the file may not.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _parseResult, snapshot, published), published))
            {
                Content = previousContent;
            }

            foreach (var (writtenSubject, _) in write.Configurations)
            {
                _parser.ForgetAppliedConfiguration(writtenSubject);
            }

            try
            {
                await OnFileChangedAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _parser.LogFailedReload(FullPath, exception);
            }

            throw;
        }

        return null;
    }

    // The block itself and every block whose lines contain its lines.
    private void ForgetAppliedBlocksAround(MarkdownPage page, MarkdownBlock block, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects)
    {
        foreach (var candidate in page.AllBlocks)
        {
            if (candidate.Line <= block.Line && candidate.EndLine >= block.EndLine &&
                blockSubjects.TryGetValue(candidate.Index, out var subject))
            {
                _parser.ForgetAppliedBlock(subject);
            }
        }
    }

    private void ForgetAppliedConfigurations(MarkdownBlock block, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects)
    {
        if (blockSubjects.TryGetValue(block.Index, out var subject))
        {
            _parser.ForgetAppliedConfiguration(subject);
        }

        foreach (var child in block.Children)
        {
            ForgetAppliedConfigurations(child, blockSubjects);
        }
    }

    // The index of the block whose subject is the subject or holds it in its configuration, for example a new grid
    // cell that has no block of its own yet, or -1.
    private static int FindBlock(IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, IInterceptorSubject subject)
    {
        var blockIndex = -1;
        subject.TryGetConfigurationOwner(candidate =>
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

        return blockIndex;
    }

    private sealed class WriteOfFlow
    {
        public volatile bool IsFinished;
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

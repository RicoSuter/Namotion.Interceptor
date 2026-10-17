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

    // The write of this page that the flow runs in, so that a write from inside it is refused instead of waiting
    // for _writeLock. Off the worker, the reload after a write or a refused write-back applies configuration within the write.
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

            // A full fence, because a volatile write lets later writes pass it. With the fence before the read of
            // the flag in WriteConfigurationAsync, a write-back that sees a slot value this parse changed also sees
            // the flag.
            _isGraphAheadOfSnapshot = true;
            Thread.MemoryBarrier();
            var result = await _parser.ParseAsync(content, this, Children, cancellationToken);

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

            // Without the flow of the caller, which is the worker of the storage or a refused write-back, as for
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

    // Reads from storage rather than Content, which lags behind an external edit until its file event arrives.
    private async Task<string> ReadSourceUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = await ReadAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // Not cancelled once the content is decided: a cancelled reload after the write would leave the parse snapshot of
    // the old content, which a later configuration write-back would write back.
    private Task WriteUnlockedAsync(string content) =>
        Storage.WriteBlobAsync(FullPath, Encoding.UTF8.GetBytes(content), CancellationToken.None);

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

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
            try
            {
                await OnFileChangedAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _parser.LogFailedReload(FullPath, exception);
            }

            return false;
        }

        // After the graph was read and before anything is published: the blocks were compared with slot values
        // that a reload may have changed since the snapshot was parsed. A reload that published since then fails
        // the exchange below. A full fence, because a volatile read lets the earlier reads of the graph pass it
        // (see OnFileChangedAsync).
        Thread.MemoryBarrier();
        if (write.HasInsertedOrRemovedBlocks && _isGraphAheadOfSnapshot)
        {
            _parser.LogRefusedWrite(FullPath, "Blocks cannot be inserted or removed until the page has been read again.");
            return false;
        }

        // The text was computed from the snapshot, so it would undo what a reload published since then.
        var published = snapshot with { Page = write.Page, BlockSubjects = write.BlockSubjects };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _parseResult, published, snapshot), snapshot))
        {
            _parser.LogRefusedWrite(FullPath, "The page changed while the configuration was written; save again.");
            return false;
        }

        // The text of the block and of the blocks around it changes in the file, so a later read of the text that
        // they had before is a change that must be applied, also when the read inside the write is skipped or fails.
        // The blocks inside it keep their text.
        ForgetAppliedBlocksAround(page, page.AllBlocks[blockIndex], snapshot.BlockSubjects);

        // Before the write, because the storage reloads this file from inside the write. The reload
        // must find the keys that are now in the file, or a value that became null would recreate the subject.
        foreach (var (writtenSubject, configurationJson) in write.Configurations)
        {
            _parser.RememberConfiguration(writtenSubject, configurationJson);
        }

        var content = write.Page.Source;
        Content = content;

        await WriteUnlockedAsync(content);
        return true;
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

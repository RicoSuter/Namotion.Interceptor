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
    // it would write what a running reload applied in place of the change it was called for, and the storage
    // reloads the file as part of the write, which would wait for the write-back that waits for the write.
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // A reload changes the subjects it reuses before it publishes its snapshot, and it reuses the values of a
    // slot by their position. Between the two, and after a reload that failed once it had begun, the slot
    // values of the graph do not belong to the blocks of the snapshot, so a write-back must not insert or
    // remove a block then. A reload that publishes while a write-back works makes its exchange fail instead.
    private volatile bool _isGraphAheadOfSnapshot;

    // Serializes the write-backs of the page: each one computes its text from the snapshot that the one before it
    // left, and the reload inside that write has applied the page before the next one reads the graph. Not the
    // reload lock, which the reload inside the write takes while this one is held.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // The write-back of this page that the flow runs in, so that a save from inside it is refused instead of waiting
    // for _writeLock. Off the worker, the reload after a refused write applies configuration within the write.
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

    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions", Icon = "Description")]
    public string? ReadRawContent() => Content;

    [Query(Title = "Read Evaluated Content", Description = "Returns the markdown content with {{ path }} expressions evaluated against the current object graph", Icon = "Preview")]
    public string? ReadEvaluatedContent() => _parser.RenderContent(Content, this);

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

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
    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        // From work on the worker or within a write of this page, such as the configuration that the reload inside a
        // save applies, the call would wait for the lock that the save holds while it waits for that work. The worker
        // would refuse it later.
        if (Storage is FluentStorageContainer { IsCallFromWithinWorkerItem: true } ||
            _writeOfFlow.Value is { IsFinished: false })
        {
            throw new InvalidOperationException(StorageWorker.CallFromWithinItemMessage);
        }

        await _writeLock.WaitAsync(cancellationToken);
        var write = new WriteOfFlow();
        _writeOfFlow.Value = write;
        try
        {
            return await WriteBlockAsync(subject, cancellationToken);
        }
        finally
        {
            // A flow that the write started and did not await keeps the value, and may save once the write is over.
            write.IsFinished = true;
            _writeLock.Release();
        }
    }

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

        // Before the write, because the storage reloads this file from inside WriteAsync. The reload
        // must find the keys that are now in the file, or a value that became null would recreate the subject.
        foreach (var (writtenSubject, configurationJson) in write.Configurations)
        {
            _parser.RememberConfiguration(writtenSubject, configurationJson);
        }

        var content = write.Page.Source;
        Content = content;

        await Storage.WriteBlobAsync(FullPath, Encoding.UTF8.GetBytes(content), cancellationToken);
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

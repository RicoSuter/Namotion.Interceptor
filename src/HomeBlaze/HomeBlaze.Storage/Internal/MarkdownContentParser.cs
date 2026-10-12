using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Turns the blocks and expressions of a markdown page into the child subjects of its <see cref="MarkdownFile"/>.
/// </summary>
public sealed partial class MarkdownContentParser
{
    private const string HtmlKeyPrefix = "Html_";
    private const string ExpressionKeyPrefix = "Expression_";

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly SubjectPathResolver _pathResolver;
    private readonly TypeProvider _typeProvider;
    private readonly ILogger<MarkdownContentParser>? _logger;

    /// <summary>
    /// Rewrites relative .md links to /pages/... route format with Children segments.
    /// A fragment after the page stays on the link. A link that is only a fragment gets the route of the
    /// page itself, which a link names with <paramref name="pageName"/>.
    /// </summary>
    private static string RewriteLink(string url, string basePath, string pageName)
    {
        // Skip absolute URLs
        if (string.IsNullOrEmpty(url) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("/"))
        {
            return url;
        }

        // The browser resolves a link against the base address of the application and not against the
        // address of the page, so a fragment alone would lead to the start page.
        var fragmentStart = url.IndexOf('#');
        var isFragmentOnly = fragmentStart == 0;
        var pagePath = isFragmentOnly ? pageName : fragmentStart < 0 ? url : url[..fragmentStart];

        // Only process .md links
        if (!isFragmentOnly && !pagePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Resolve relative path against base path
        var resolvedPath = ResolvePath(basePath, pagePath);

        // Convert to /pages/... format with Children segments
        // e.g., "foo/bar/baz.md" => "/pages/foo/bar/baz.md"
        var segments = resolvedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder("/pages");

        foreach (var segment in segments)
        {
            result.Append("/");
            result.Append(segment);
        }

        if (fragmentStart >= 0)
        {
            result.Append(url, fragmentStart, url.Length - fragmentStart);
        }

        return result.ToString();
    }

    /// <summary>
    /// Gets a file name as a link has to write it: with the characters escaped that end the path of a URL
    /// or start an escape. Spaces and other characters are escaped when the link is rendered, as for a
    /// link that is written on the page.
    /// </summary>
    private static string EscapeFileNameForLink(string fileName) =>
        fileName.Replace("%", "%25").Replace("#", "%23").Replace("?", "%3F");

    /// <summary>
    /// Computes the base path used to resolve relative links in markdown.
    /// A markdown file is parsed before it is placed in the tree, so its own graph path is not available yet.
    /// The graph path of its storage container is, and is combined with the directory of the file within the
    /// storage.
    /// </summary>
    private string GetLinkBasePath(MarkdownFile parent)
    {
        var fileDirectory = Path.GetDirectoryName(parent.FullPath)?.Replace('\\', '/') ?? string.Empty;

        if (parent.Storage is not IInterceptorSubject storageSubject
            || _pathResolver.GetPath(storageSubject, PathStyle.Route) is not { } storageGraphPath)
        {
            return fileDirectory;
        }

        var trimmedStorage = storageGraphPath.TrimEnd('/');
        var trimmedDirectory = fileDirectory.Trim('/');
        return string.IsNullOrEmpty(trimmedDirectory)
            ? trimmedStorage
            : $"{trimmedStorage}/{trimmedDirectory}";
    }

    /// <summary>
    /// Resolves a relative path against a base path, handling .. navigation.
    /// </summary>
    private static string ResolvePath(string basePath, string relativePath)
    {
        // Normalize to forward slashes
        basePath = basePath.Replace('\\', '/').TrimStart('/');
        relativePath = relativePath.Replace('\\', '/');

        // Remove ./ prefix
        if (relativePath.StartsWith("./"))
        {
            relativePath = relativePath[2..];
        }

        // Split base path into segments
        var baseSegments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Process relative path
        var relativeSegments = relativePath.Split('/');
        foreach (var segment in relativeSegments)
        {
            if (segment == "..")
            {
                if (baseSegments.Count > 0)
                {
                    baseSegments.RemoveAt(baseSegments.Count - 1);
                }
            }
            else if (segment != "." && !string.IsNullOrEmpty(segment))
            {
                baseSegments.Add(segment);
            }
        }

        return string.Join("/", baseSegments);
    }

    public MarkdownContentParser(
        ConfigurableSubjectSerializer serializer,
        SubjectPathResolver pathResolver,
        TypeProvider typeProvider,
        ILogger<MarkdownContentParser>? logger = null)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _typeProvider = typeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Renders markdown content by replacing each {{ path }} expression that
    /// <see cref="MarkdownSegmentBuilder.FindExpressions"/> finds with its resolved value: also one in the front matter,
    /// on the head line of a block or in an invalid block, which the page itself does not evaluate. An expression inside
    /// markdown code is kept as written, and everything else stays as written.
    /// Uses the same resolution semantics as <see cref="RenderExpression"/> (in the scope of the markdown file).
    /// </summary>
    /// <param name="content">The markdown content.</param>
    /// <param name="parent">The markdown file the expressions are relative to.</param>
    /// <param name="includeSourcePaths">When true, each resolved value is followed by the absolute canonical path of
    /// its property in parentheses. An expression that does not resolve to a property renders as without it.</param>
    public string? RenderContent(string? content, MarkdownFile parent, bool includeSourcePaths = false)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        StringBuilder? rendered = null;
        var copiedLength = 0;
        foreach (var expression in MarkdownSegmentBuilder.FindExpressions(MarkdownPageParser.Parse(content)))
        {
            rendered ??= new StringBuilder(content.Length);
            rendered.Append(content, copiedLength, expression.Span.Start - copiedLength);
            try
            {
                rendered.Append(RenderExpressionValue(expression.Path, parent, includeSourcePaths));
            }
            catch
            {
                rendered.Append(content, expression.Span.Start, expression.Span.Length);
            }

            copiedLength = expression.Span.End;
        }

        return rendered is null
            ? content
            : rendered.Append(content, copiedLength, content.Length - copiedLength).ToString();
    }

    private string? RenderExpressionValue(string path, MarkdownFile parent, bool includeSourcePaths)
    {
        var property = _pathResolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, parent);
        var valueText = property?.GetValue()?.ToString();
        if (!includeSourcePaths || property is null || GetAbsolutePath(property) is not { } sourcePath)
        {
            return valueText;
        }

        return $"{valueText} ({sourcePath})";
    }

    private string? GetAbsolutePath(RegisteredSubjectProperty property)
    {
        var subjectPath = _pathResolver.GetPath(property.Subject, PathStyle.Canonical);
        return subjectPath is null ? null : $"{subjectPath.TrimEnd('/')}/{property.Name}";
    }

    /// <summary>
    /// Parses page content and reconciles it with the existing children.
    /// </summary>
    public async Task<MarkdownParseResult> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return MarkdownParseResult.Empty;
        }

        var page = MarkdownPageParser.Parse(content);
        var basePath = GetLinkBasePath(parent);
        var pageName = EscapeFileNameForLink(Path.GetFileName(parent.FullPath));
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath, pageName));

        var state = new ParseState(parent, page.AllBlocks);
        try
        {
            var children = await BuildChildrenAsync(segments, existingChildren, state, cancellationToken);
            return new MarkdownParseResult(children, page, state.BlockSubjects, state.NamedSubjects);
        }
        catch
        {
            // Nothing of this parse is published, so nothing would ever detach the subjects it created.
            ReleaseCreatedSince(state, 0);
            throw;
        }
    }

    private async Task<IDictionary<string, IInterceptorSubject>> BuildChildrenAsync(
        IReadOnlyList<MarkdownSegment> segments,
        IDictionary<string, IInterceptorSubject> existingChildren,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, IInterceptorSubject>();

        for (var index = 0; index < segments.Count; index++)
        {
            switch (segments[index])
            {
                case HtmlMarkdownSegment html:
                    AddHtml(children, existingChildren, index, html.Html);
                    break;

                case ExpressionMarkdownSegment expression:
                    var expressionKey = $"{ExpressionKeyPrefix}{index}_{ComputeHash(expression.Path)}";
                    children[expressionKey] =
                        existingChildren.TryGetValue(expressionKey, out var existingExpression) && existingExpression is RenderExpression
                            ? existingExpression
                            : new RenderExpression(expression.Path, state.Page, _pathResolver);
                    break;

                case BlockMarkdownSegment block:
                    var (key, subject, error) = await CreateWidgetAsync(block, children, existingChildren, index, state, cancellationToken);
                    if (subject is null)
                    {
                        AddHtml(children, existingChildren, index, error!.Render());
                    }
                    else
                    {
                        children[key!] = subject;
                    }

                    break;
            }
        }

        return children;
    }

    private static void AddHtml(
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        string html)
    {
        // The index is part of the key because the same HTML can occur several times.
        var key = $"{HtmlKeyPrefix}{index}_{ComputeHash(html)}";
        children[key] = existingChildren.TryGetValue(key, out var existing) && existing is HtmlSegment
            ? existing
            : new HtmlSegment(html);
    }

    private async Task<(string? Key, IInterceptorSubject? Subject, BlockError? Error)> CreateWidgetAsync(
        BlockMarkdownSegment segment,
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var block = segment.Block;
        var head = block.Head!;

        if (head.Id is not null && !state.OwnsName(block))
        {
            return (null, null, new BlockError(block.Line, FormatUsedTwice(head.Id)));
        }

        if (block.Kind == MarkdownBlockKind.Slot)
        {
            return (null, null, new BlockError(
                block.Line,
                $"'{head.Name}' is not a slot at this position. A slot block must be directly inside the widget that declares it."));
        }

        var type = FindType(head.Name);
        if (type is null)
        {
            return (null, null, new BlockError(
                block.Line, $"Unknown type '{head.Name}'. Use the full type name including the namespace."));
        }

        if (!typeof(IConfigurable).IsAssignableFrom(type))
        {
            return (null, null, new BlockError(
                block.Line,
                $"The type '{head.Name}' cannot be used as a block because it does not implement {nameof(IConfigurable)}."));
        }

        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            return (null, null, new BlockError(
                block.Line,
                $"The type '{head.Name}' cannot be created because it is abstract, an interface or an open generic type."));
        }

        // An id can equal the positional key of another block, for example #Card_3.
        var key = MarkdownSubjectKeys.GetContentKey(head.Id, type, index);
        if (children.ContainsKey(key))
        {
            return (null, null, new BlockError(block.Line, FormatUsedTwice(key)));
        }

        existingChildren.TryGetValue(key, out var existing);
        var (subject, error) = await CreateBlockSubjectAsync(
            type, $"The type '{head.Name}'", segment, existing, state, cancellationToken);

        return (subject is null ? null : key, subject, error);
    }

    /// <summary>Gets the type that a widget block names with its full name, or null when there is none.</summary>
    internal Type? FindType(string fullName) => _typeProvider.Types.FirstOrDefault(candidate => candidate.FullName == fullName);

    /// <summary>Gets whether the path resolves to a subject from <paramref name="from"/> the way a page resolves it.</summary>
    internal bool ResolvesToSubject(string path, IInterceptorSubject from)
    {
        try
        {
            return _pathResolver.ResolveScopedSubject(path, PathStyle.Canonical, from) is not null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string FormatUsedTwice(string name) => $"The name '{name}' is used twice on this page.";

    /// <summary>
    /// Creates the subject of a widget block or of a slot block with a concrete type, or updates
    /// <paramref name="existing"/> in place, and fills its slots from the content of the block.
    /// <paramref name="typeDescription"/> is how an error message names the type, for example "The type 'X'".
    /// </summary>
    private async Task<(IInterceptorSubject? Subject, BlockError? Error)> CreateBlockSubjectAsync(
        Type type,
        string typeDescription,
        BlockMarkdownSegment segment,
        IInterceptorSubject? existing,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var block = segment.Block;
        var registrationCount = state.RegistrationCount;
        var createdCount = state.CreatedCount;

        BlockError? error;
        try
        {
            // Everything that can be checked without an instance is checked before one is created or updated,
            // so that a block which can never be filled runs no constructor and changes no subject on the page.
            var slots = SlotMap.For(type);
            if (slots.Error is not null)
            {
                return (null, new BlockError(block.Line, slots.Error));
            }

            if (!TryBuildConfiguration(type, block, out var configuration, out var configurationError))
            {
                return (null, new BlockError(block.Line, configurationError!));
            }

            if (!TryArrangeContent(type, slots, segment, state.OwnsName, out var content, out var contentError))
            {
                return (null, contentError);
            }

            var (subject, valueError) = await CreateOrUpdateAsync(type, existing, configuration, state, cancellationToken);
            if (valueError is not null)
            {
                error = new BlockError(block.Line, valueError);
            }
            else if (subject is null)
            {
                return (null, new BlockError(
                    block.Line, $"{typeDescription} could not be created. It must be a registered configurable subject type."));
            }
            else
            {
                error = await ApplyContentAsync(subject, slots, content, state, cancellationToken);
                if (error is null)
                {
                    state.Register(block, block.Head!.Id, subject);
                    return (subject, null);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Constructors, ApplyConfigurationAsync and property setters run foreign code. One failing block must not
            // take the page down. A cancellation that the caller did not request is such a failure too, for example a
            // timeout inside the subject. The page shows the message only, so the log is where the stack trace is.
            _logger?.LogWarning(
                exception,
                "The block at line {Line} of the page {Path} could not be created or updated.",
                block.Line,
                state.Page.FullPath);

            error = new BlockError(block.Line, $"{typeDescription} could not be created or updated: {exception.Message}");
        }

        // The block ends as an error box, so the subjects that blocks nested in it registered are not on the page.
        state.RemoveRegistrationsSince(registrationCount);
        ReleaseCreatedSince(state, createdCount);
        return (null, error);
    }

    /// <summary>
    /// Releases the subjects that were created after <see cref="ParseState.CreatedCount"/> had the given value,
    /// because they do not get onto the page.
    /// </summary>
    private void ReleaseCreatedSince(ParseState state, int createdCount)
    {
        // In the order of creation: a block subject is created before the subjects in its content, and
        // releasing it detaches those with it. A subject that still has a reference afterwards was put into
        // a subject that this parse reused. That one is on the page, so the release leaves its content alone.
        foreach (var subject in state.TakeCreatedSince(createdCount))
        {
            try
            {
                // Detaching runs lifecycle handlers as attaching does. Without the execution flow, see
                // CreateOrUpdateAsync.
                using (ExecutionContext.SuppressFlow())
                {
                    _serializer.Release(subject);
                }
            }
            catch (Exception exception)
            {
                // Detaching runs the lifecycle handlers of the context and of the subject. A failure there must
                // not keep the other subjects from being released, turn a failed block into a failed page, or
                // replace the exception that ended the parse.
                _logger?.LogWarning(
                    exception,
                    "A subject of the type {Type} that the page {Path} created and did not use could not be released.",
                    subject.GetType().FullName,
                    state.Page.FullPath);
            }
        }
    }

    private static string ComputeHash(string content)
    {
        var byteCount = Encoding.UTF8.GetByteCount(content);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(content, buffer);
            Span<byte> hashBuffer = stackalloc byte[32];
            SHA256.HashData(buffer.AsSpan(0, bytesWritten), hashBuffer);
            return Convert.ToHexStringLower(hashBuffer[..8]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>What an error box shows: the line of the block to fix and the message.</summary>
    private sealed record BlockError(int Line, string Message)
    {
        /// <summary>The box after this one, when several boxes replace a block.</summary>
        public BlockError? Next { get; init; }

        public string Render()
        {
            var box = MarkdownErrorBox.Render(Line, Message);
            return Next is null ? box : box + Next.Render();
        }
    }

    private sealed class ParseState
    {
        private readonly List<(int BlockIndex, string? Id)> _registrations = [];
        private readonly List<IInterceptorSubject> _created = [];
        private readonly Dictionary<string, int> _nameOwners = new();

        public ParseState(MarkdownFile page, IReadOnlyList<MarkdownBlock> blocksInDocumentOrder)
        {
            Page = page;

            // Decided before anything is built: slot blocks are built before the loose content around them, and
            // the blocks inside a block that ends as an error box are not built at all.
            foreach (var block in blocksInDocumentOrder)
            {
                if (block.Head?.Id is { } id)
                {
                    _nameOwners.TryAdd(id, block.Index);
                }
            }
        }

        public MarkdownFile Page { get; }

        public Dictionary<int, IInterceptorSubject> BlockSubjects { get; } = new();

        public Dictionary<string, IInterceptorSubject> NamedSubjects { get; } = new();

        public int RegistrationCount => _registrations.Count;

        /// <summary>How many subjects this parse created so far and did not release.</summary>
        public int CreatedCount => _created.Count;

        /// <summary>
        /// Records a subject that this parse created with the context. Until it is in the published result,
        /// it is detached only when it is released.
        /// </summary>
        public void AddCreated(IInterceptorSubject subject) => _created.Add(subject);

        /// <summary>
        /// Removes and returns the subjects created after <see cref="CreatedCount"/> had the given value,
        /// in the order they were created.
        /// </summary>
        public IReadOnlyList<IInterceptorSubject> TakeCreatedSince(int createdCount)
        {
            if (createdCount == _created.Count)
            {
                return [];
            }

            var created = _created.GetRange(createdCount, _created.Count - createdCount);
            _created.RemoveRange(createdCount, created.Count);
            return created;
        }

        /// <summary>
        /// Whether the block is the first one in the document with its name. Every other block
        /// with that name is an error, whatever becomes of the first one.
        /// </summary>
        public bool OwnsName(MarkdownBlock block) =>
            block.Head?.Id is not { } id || _nameOwners[id] == block.Index;

        public void Register(MarkdownBlock block, string? id, IInterceptorSubject subject)
        {
            BlockSubjects[block.Index] = subject;
            if (id is not null)
            {
                NamedSubjects[id] = subject;
            }

            _registrations.Add((block.Index, id));
        }

        /// <summary>
        /// Removes the subjects registered after <see cref="RegistrationCount"/> had the given value.
        /// Their blocks still own their names.
        /// </summary>
        public void RemoveRegistrationsSince(int registrationCount)
        {
            for (var index = _registrations.Count - 1; index >= registrationCount; index--)
            {
                var (blockIndex, id) = _registrations[index];
                BlockSubjects.Remove(blockIndex);
                if (id is not null)
                {
                    NamedSubjects.Remove(id);
                }
            }

            _registrations.RemoveRange(registrationCount, _registrations.Count - registrationCount);
        }
    }
}

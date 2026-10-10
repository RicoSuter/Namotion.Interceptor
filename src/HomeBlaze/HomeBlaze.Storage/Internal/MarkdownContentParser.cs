using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

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

    /// <summary>
    /// Rewrites relative .md links to /pages/... route format with Children segments.
    /// </summary>
    private static string RewriteLink(string url, string basePath)
    {
        // Skip absolute URLs and anchors
        if (string.IsNullOrEmpty(url) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("#") ||
            url.StartsWith("/"))
        {
            return url;
        }

        // Only process .md links
        if (!url.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Resolve relative path against base path
        var resolvedPath = ResolvePath(basePath, url);

        // Convert to /pages/... format with Children segments
        // e.g., "foo/bar/baz.md" => "/pages/foo/bar/baz.md"
        var segments = resolvedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder("/pages");

        foreach (var segment in segments)
        {
            result.Append("/");
            result.Append(segment);
        }

        return result.ToString();
    }

    /// <summary>
    /// Computes the base path used to resolve relative links in markdown.
    /// During initial scan, markdown files are parsed BEFORE being placed in their parent's Children,
    /// so their own graph path isn't available yet. Use the storage container's graph path (which IS
    /// attached by the time ScanAsync runs) combined with the file's storage-local directory.
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
        TypeProvider typeProvider)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _typeProvider = typeProvider;
    }

    // Shared with the segment builder so that rendering the raw content and splitting the HTML
    // agree on what a {{...}} is.
    [GeneratedRegex(MarkdownSegmentBuilder.ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Renders markdown content by replacing {{ path }} expressions with their resolved values.
    /// Embedded subject blocks are kept as-is. Frontmatter is preserved.
    /// Uses the same resolution semantics as <see cref="RenderExpression"/> (in the scope of the markdown file).
    /// </summary>
    public string? RenderContent(string? content, MarkdownFile parent)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        return ExpressionRegex().Replace(content, match =>
        {
            var path = match.Groups[1].Value;
            try
            {
                var value = _pathResolver.ResolveScopedValue(path, PathStyle.Canonical, parent);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return match.Value;
            }
        });
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
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath));

        var state = new ParseState(parent, page.AllBlocks);
        var children = await BuildChildrenAsync(segments, existingChildren, state, cancellationToken);
        return new MarkdownParseResult(children, page, state.BlockSubjects, state.NamedSubjects);
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
                        AddHtml(children, existingChildren, index, MarkdownErrorBox.Render(error!.Line, error.Message));
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

        var type = _typeProvider.Types.FirstOrDefault(candidate => candidate.FullName == head.Name);
        if (type is null || !typeof(IConfigurable).IsAssignableFrom(type))
        {
            return (null, null, new BlockError(
                block.Line, $"Unknown type '{head.Name}'. Use the full type name including the namespace."));
        }

        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            return (null, null, new BlockError(
                block.Line,
                $"The type '{head.Name}' cannot be created because it is abstract, an interface or an open generic type."));
        }

        // An id can equal the positional key of another block, for example #Card_3.
        var key = head.Id ?? $"{type.Name}_{index}";
        if (children.ContainsKey(key))
        {
            return (null, null, new BlockError(block.Line, FormatUsedTwice(key)));
        }

        existingChildren.TryGetValue(key, out var existing);
        var (subject, error) = await CreateBlockSubjectAsync(
            type, $"The type '{head.Name}'", segment, existing, state, cancellationToken);

        return (subject is null ? null : key, subject, error);
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

        BlockError? error;
        try
        {
            // Before anything is created, so that a type that can never be filled runs no constructor.
            var slots = SlotMap.For(type);
            if (slots.Error is not null)
            {
                return (null, new BlockError(block.Line, slots.Error));
            }

            if (!TryBuildConfiguration(type, block, out var configuration, out var configurationError))
            {
                return (null, new BlockError(block.Line, configurationError!));
            }

            var subject = await CreateOrUpdateAsync(type, existing, configuration, cancellationToken);
            if (subject is null)
            {
                return (null, new BlockError(
                    block.Line, $"{typeDescription} could not be created. It must be a registered configurable subject type."));
            }

            error = await ApplyContentAsync(subject, type, slots, segment, state, cancellationToken);
            if (error is null)
            {
                state.Register(block, block.Head!.Id, subject);
                return (subject, null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Constructors, ApplyConfigurationAsync and property setters run foreign code. One failing block must not
            // take the page down. A cancellation that the caller did not request is such a failure too, for example a
            // timeout inside the subject.
            error = new BlockError(block.Line, $"{typeDescription} could not be created or updated: {exception.Message}");
        }

        // The block ends as an error box, so the subjects that blocks nested in it registered are not on the page.
        state.RemoveRegistrationsSince(registrationCount);
        return (null, error);
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
    private sealed record BlockError(int Line, string Message);

    private sealed class ParseState
    {
        private readonly List<(int BlockIndex, string? Id)> _registrations = [];
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

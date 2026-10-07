using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Markdig;
using Markdig.Renderers;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Parses markdown content for embedded subjects and expressions.
/// Extracted from MarkdownFile to maintain single responsibility.
/// </summary>
public sealed partial class MarkdownContentParser
{
    // Constants for key prefixes
    private const string HtmlKeyPrefix = "Html_";
    private const string ExpressionKeyPrefix = "Expression_";
    private const string SubjectMarkerPrefix = "<!--SUBJECT:";
    private const string SubjectMarkerSuffix = "-->";

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<MarkdownContentParser>? _logger;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Converts markdown to HTML with relative link rewriting.
    /// </summary>
    private static string ToHtml(string markdown, string basePath)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            LinkRewriter = url => RewriteLink(url, basePath)
        };
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

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
        ILogger<MarkdownContentParser>? logger = null)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    // Source-generated regexes for performance.
    // ExpressionPattern is shared between SegmentMarkerRegex (HTML post-processing) and
    // ExpressionRegex (raw-content rendering) so the two code paths agree on what a {{...}} is.
    private const string ExpressionPattern = @"\{\{\s*([^}]+)\s*\}\}";

    [GeneratedRegex(@"```subject\(([^)]+)\)\s*\n([\s\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex SubjectBlockRegex();

    [GeneratedRegex(@"(<!--SUBJECT:([^>]+)-->|" + ExpressionPattern + ")")]
    private static partial Regex SegmentMarkerRegex();

    [GeneratedRegex(ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Renders markdown content by replacing {{ path }} expressions with their resolved values.
    /// Embedded subject blocks are kept as-is. Frontmatter is preserved.
    /// Uses the same resolution semantics as <see cref="RenderExpression"/> (relative to the markdown file).
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
                var value = _pathResolver.ResolveValue(path, PathStyle.Canonical, relativeTo: parent);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Parses markdown content and reconciles with existing children.
    /// </summary>
    public async Task<IDictionary<string, IInterceptorSubject>> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            parent.UnresolvedSubjectTypeNames = MarkdownFile.NoTypeNames;
            parent.SubjectBlockJson = MarkdownFile.NoSubjectBlockJson;
            return new Dictionary<string, IInterceptorSubject>();
        }

        var contentWithoutFrontmatter = FrontmatterParser.GetContentAfterFrontmatter(content);
        var (markdownWithMarkers, subjectBlocks) = ExtractSubjectBlocks(contentWithoutFrontmatter);

        var basePath = GetLinkBasePath(parent);
        var html = ToHtml(markdownWithMarkers, basePath);
        var segments = ParseHtmlSegments(html, subjectBlocks);
        return await ReconcileChildrenAsync(segments, parent, existingChildren, cancellationToken);
    }

    private static (string markdown, Dictionary<string, string> subjectBlocks) ExtractSubjectBlocks(string markdown)
    {
        var subjectBlocks = new Dictionary<string, string>();

        var result = SubjectBlockRegex().Replace(markdown, match =>
        {
            var name = match.Groups[1].Value;
            var json = match.Groups[2].Value.Trim();
            subjectBlocks[name] = json;
            return $"{SubjectMarkerPrefix}{name}{SubjectMarkerSuffix}";
        });

        return (result, subjectBlocks);
    }

    private static List<ParsedSegment> ParseHtmlSegments(string html, Dictionary<string, string> subjectBlocks)
    {
        var segments = new List<ParsedSegment>();
        var lastIndex = 0;

        foreach (Match match in SegmentMarkerRegex().Matches(html))
        {
            // Add HTML before this match
            if (match.Index > lastIndex)
            {
                var htmlSegment = html.Substring(lastIndex, match.Index - lastIndex);
                segments.Add(new HtmlParsedSegment(htmlSegment));
            }

            if (match.Groups[2].Success)
            {
                // Subject marker
                var name = match.Groups[2].Value;
                if (subjectBlocks.TryGetValue(name, out var json))
                {
                    segments.Add(new SubjectParsedSegment(name, json));
                }
            }
            else if (match.Groups[3].Success)
            {
                // Expression
                var path = match.Groups[3].Value.Trim();
                segments.Add(new ExpressionParsedSegment(path));
            }

            lastIndex = match.Index + match.Length;
        }

        // Add remaining HTML
        if (lastIndex < html.Length)
        {
            segments.Add(new HtmlParsedSegment(html.Substring(lastIndex)));
        }

        return segments;
    }

    private async Task<IDictionary<string, IInterceptorSubject>> ReconcileChildrenAsync(
        List<ParsedSegment> segments,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> oldChildren,
        CancellationToken cancellationToken)
    {
        var newChildren = new Dictionary<string, IInterceptorSubject>();
        var subjectBlockJson = new Dictionary<string, string>();
        HashSet<string>? unresolvedTypeNames = null;
        var segmentIndex = 0;

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case HtmlParsedSegment html:
                    // Include index in key to handle duplicate HTML segments (e.g., "</td><td>" between cells)
                    var htmlKey = $"{HtmlKeyPrefix}{segmentIndex}_{ComputeHash(html.Html)}";
                    if (oldChildren.TryGetValue(htmlKey, out var existingHtml) && existingHtml is HtmlSegment)
                    {
                        newChildren[htmlKey] = existingHtml;
                    }
                    else
                    {
                        newChildren[htmlKey] = new HtmlSegment(html.Html);
                    }
                    break;

                case ExpressionParsedSegment expr:
                    // Include index in key to handle duplicate expressions
                    var exprKey = $"{ExpressionKeyPrefix}{segmentIndex}_{ComputeHash(expr.Path)}";
                    if (oldChildren.TryGetValue(exprKey, out var existingExpr) && existingExpr is RenderExpression)
                    {
                        newChildren[exprKey] = existingExpr;
                    }
                    else
                    {
                        newChildren[exprKey] = new RenderExpression(expr.Path, parent, _pathResolver);
                    }
                    break;

                case SubjectParsedSegment subj:
                    var typeName = ExtractDiscriminator(subj.Json);
                    if (oldChildren.TryGetValue(subj.Name, out var existing) &&
                        existing.GetType().FullName == typeName)
                    {
                        // Same key + same type: keep the subject, and reconfigure it only when its JSON changed
                        if (!parent.SubjectBlockJson.TryGetValue(subj.Name, out var previousJson) || previousJson != subj.Json)
                        {
                            _serializer.UpdateConfiguration(existing, subj.Json);
                            if (existing is IConfigurable configurable)
                            {
                                await configurable.ApplyConfigurationAsync(cancellationToken);
                            }
                        }

                        newChildren[subj.Name] = existing;
                        subjectBlockJson[subj.Name] = subj.Json;
                    }
                    else
                    {
                        // Create new subject
                        var newSubject = _serializer.Deserialize(subj.Json);
                        if (newSubject != null)
                        {
                            // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                            newChildren[subj.Name] = (IInterceptorSubject)newSubject;
                            subjectBlockJson[subj.Name] = subj.Json;
                        }
                        else if (typeName is { Length: > 0 } &&
                                 (unresolvedTypeNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(typeName) &&
                                 !parent.UnresolvedSubjectTypeNames.Contains(typeName))
                        {
                            // Expected while plugins load, so it only warns once startup has completed; storages
                            // warn about the blocks still missing then.
                            _logger?.Log(
                                IsStartupCompleted(parent) ? LogLevel.Warning : LogLevel.Information,
                                "Subject block {Name} in {Path} is not shown because its type {Type} is not loaded. It appears once the type is loaded.",
                                subj.Name, parent.FullPath, typeName);
                        }
                    }
                    break;
            }

            segmentIndex++;
        }

        // The storage refreshes the file once one of these types is loaded, which adds the skipped blocks.
        parent.UnresolvedSubjectTypeNames = unresolvedTypeNames ?? MarkdownFile.NoTypeNames;
        parent.SubjectBlockJson = subjectBlockJson;
        return newChildren;
    }

    private static bool IsStartupCompleted(MarkdownFile parent)
    {
        // The file itself is not attached yet while its storage scans, the storage is.
        return parent.Storage is not IInterceptorSubject storage || storage.Context.IsStartupCompleted();
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

    private static string? ExtractDiscriminator(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("$type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Invalid JSON has no type to wait for.
        }

        return null;
    }

    // Parsed segment types - internal for testing
    internal abstract record ParsedSegment;
    internal sealed record HtmlParsedSegment(string Html) : ParsedSegment;
    internal sealed record ExpressionParsedSegment(string Path) : ParsedSegment;
    internal sealed record SubjectParsedSegment(string Name, string Json) : ParsedSegment;
}

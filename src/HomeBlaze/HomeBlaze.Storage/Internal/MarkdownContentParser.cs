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
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

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
        SubjectPathResolver pathResolver)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
    }

    // Source-generated regexes for performance.
    // ExpressionPattern is shared between SegmentMarkerRegex (HTML post-processing) and
    // ExpressionRegex (raw-content rendering) so the two code paths agree on what a {{...}} is.
    private const string ExpressionPattern = @"\{\{\s*([^}]+)\s*\}\}";

    /// <summary>Matches an embedded subject block: group 1 is its name, group 2 its JSON.</summary>
    [GeneratedRegex(@"```subject\(([^)]+)\)\s*\n([\s\S]*?)```", RegexOptions.Singleline)]
    internal static partial Regex SubjectBlockRegex();

    [GeneratedRegex(@"(<!--SUBJECT:([^>]+)-->|" + ExpressionPattern + ")")]
    private static partial Regex SegmentMarkerRegex();

    [GeneratedRegex(ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Renders markdown content by replacing {{ path }} expressions with their resolved values.
    /// Embedded subject blocks are kept as-is. Frontmatter is preserved.
    /// Uses the same resolution semantics as <see cref="RenderExpression"/> (relative to the markdown file).
    /// </summary>
    /// <param name="content">The markdown content.</param>
    /// <param name="parent">The markdown file the expressions are relative to.</param>
    /// <param name="includeSourcePaths">When true, each resolved value is followed by the absolute canonical path of
    /// its property in parentheses. An expression that does not resolve to a property renders as without it.</param>
    public string? RenderContent(string? content, MarkdownFile parent, bool includeSourcePaths = false)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        return ExpressionRegex().Replace(content, match =>
        {
            // The pattern's capture group keeps the whitespace before the closing braces.
            var path = match.Groups[1].Value.Trim();
            try
            {
                var property = _pathResolver.ResolveDisplayableProperty(path, PathStyle.Canonical, relativeTo: parent);
                var valueText = property?.GetValue()?.ToString() ?? string.Empty;
                if (!includeSourcePaths || property is null || GetAbsolutePath(property) is not { } sourcePath)
                {
                    return valueText;
                }

                return $"{valueText} ({sourcePath})";
            }
            catch
            {
                return match.Value;
            }
        });
    }

    private string? GetAbsolutePath(RegisteredSubjectProperty property)
    {
        var subjectPath = _pathResolver.GetPath(property.Subject, PathStyle.Canonical);
        return subjectPath is null ? null : $"{subjectPath.TrimEnd('/')}/{property.Name}";
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
                    var typeName = ExtractTypeName(subj.Json);
                    if (oldChildren.TryGetValue(subj.Name, out var existing) &&
                        existing.GetType().FullName == typeName)
                    {
                        // Same key + same type: update and apply config
                        _serializer.UpdateConfiguration(existing, subj.Json);
                        if (existing is IConfigurable configurable)
                        {
                            await configurable.ApplyConfigurationAsync(cancellationToken);
                        }
                        newChildren[subj.Name] = existing;
                    }
                    else
                    {
                        // Create new subject
                        var newSubject = _serializer.Deserialize(subj.Json);
                        if (newSubject != null)
                        {
                            // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                            newChildren[subj.Name] = (IInterceptorSubject)newSubject;
                        }
                    }
                    break;
            }

            segmentIndex++;
        }

        return newChildren;
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

    private static string? ExtractTypeName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName, out var typeElement))
            {
                return typeElement.GetString();
            }
        }
        catch
        {
            // Invalid JSON - return null
        }
        return null;
    }

    /// <summary>
    /// Collects what in a Markdown source makes the page run code or subjects rather than show text, the way the page
    /// parses it: the embedded subject blocks, and the raw HTML, element attributes and links with a scheme other than
    /// http, https or mailto, each as its source text.
    /// </summary>
    internal static (List<string> SubjectBlocks, List<string> ActiveMarkup) GetActiveContent(string source)
    {
        var subjectBlocks = SubjectBlockRegex().Matches(source).Select(match => match.Value).ToList();

        // Every block becomes the same marker, so the markup of a page whose blocks are unchanged parses the same.
        var markdown = SubjectBlockRegex().Replace(
            FrontmatterParser.GetContentAfterFrontmatter(source), SubjectMarkerPrefix + SubjectMarkerSuffix);
        var document = Markdown.Parse(markdown, Pipeline);

        var activeMarkup = new List<string>();
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HtmlBlock htmlBlock:
                    activeMarkup.Add("html:" + htmlBlock.Lines);
                    break;
                case HtmlInline htmlInline:
                    activeMarkup.Add("html:" + htmlInline.Tag);
                    break;
                case LinkInline { Url: { } url } when !IsSafeUrl(url):
                    activeMarkup.Add("url:" + url);
                    break;
                case AutolinkInline { IsEmail: false } autolink when !IsSafeUrl(autolink.Url):
                    activeMarkup.Add("url:" + autolink.Url);
                    break;
                case LinkReferenceDefinition { Url: { } url } when !IsSafeUrl(url):
                    activeMarkup.Add("url:" + url);
                    break;
            }

            // Identifiers and classes only style the page; other attributes can hold script, for example onclick.
            if (node.TryGetAttributes() is { Properties: { Count: > 0 } properties })
            {
                activeMarkup.Add("attributes:" + string.Join(" ", properties.Select(property => property.Key + "=" + property.Value)));
            }
        }

        return (subjectBlocks, activeMarkup);
    }

    /// <summary>
    /// Whether a link target is relative or uses http, https or mailto. Browsers ignore whitespace and control
    /// characters in a scheme, so they are removed before it is compared.
    /// </summary>
    private static bool IsSafeUrl(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0 || url.AsSpan(0, colon).IndexOfAny('/', '?', '#') >= 0)
        {
            return true;
        }

        var scheme = new string(url.AsSpan(0, colon).ToArray().Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character)).ToArray());
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    // Parsed segment types - internal for testing
    internal abstract record ParsedSegment;
    internal sealed record HtmlParsedSegment(string Html) : ParsedSegment;
    internal sealed record ExpressionParsedSegment(string Path) : ParsedSegment;
    internal sealed record SubjectParsedSegment(string Name, string Json) : ParsedSegment;
}

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Navigation;
using HomeBlaze.Storage.Files;
using Markdig;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Parses markdown content for embedded subjects and expressions.
/// Extracted from MarkdownFile to maintain single responsibility.
/// </summary>
public sealed partial class MarkdownContentParser
{
    // Constants for key prefixes
    private const string HtmlKeyPrefix = "_html_";
    private const string ExpressionKeyPrefix = "_expr_";
    private const string SubjectMarkerPrefix = "<!--SUBJECT:";
    private const string SubjectMarkerSuffix = "-->";

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly SubjectPathResolver _pathResolver;
    private readonly RootManager _rootManager;
    
    // TODO: Review class

    // Shared pipeline - single instance
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public MarkdownContentParser(
        ConfigurableSubjectSerializer serializer,
        SubjectPathResolver pathResolver,
        RootManager rootManager)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _rootManager = rootManager;
    }

    // Source-generated regexes for performance
    [GeneratedRegex(@"```subject\(([^)]+)\)\s*\n([\s\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex SubjectBlockRegex();

    [GeneratedRegex(@"(<!--SUBJECT:([^>]+)-->|\{\{\s*([^}]+)\s*\}\})")]
    private static partial Regex SegmentMarkerRegex();

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

        var html = Markdown.ToHtml(markdownWithMarkers, Pipeline);
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

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case HtmlParsedSegment html:
                    var htmlKey = $"{HtmlKeyPrefix}{ComputeHash(html.Html)}";
                    if (oldChildren.TryGetValue(htmlKey, out var existingHtml) && existingHtml is Storage.Internal.HtmlSegment)
                    {
                        newChildren[htmlKey] = existingHtml;
                    }
                    else
                    {
                        newChildren[htmlKey] = new HtmlSegment(html.Html);
                    }
                    break;

                case ExpressionParsedSegment expr:
                    var exprKey = $"{ExpressionKeyPrefix}{ComputeHash(expr.Path)}";
                    if (oldChildren.TryGetValue(exprKey, out var existingExpr) && existingExpr is RenderExpression)
                    {
                        newChildren[exprKey] = existingExpr;
                    }
                    else
                    {
                        newChildren[exprKey] = new RenderExpression(expr.Path, parent, _pathResolver, _rootManager);
                    }
                    break;

                case SubjectParsedSegment subj:
                    var typeName = ExtractTypeName(subj.Json);
                    if (oldChildren.TryGetValue(subj.Name, out var existing) &&
                        existing.GetType().FullName == typeName)
                    {
                        // Same key + same type: update and apply config
                        _serializer.UpdateConfiguration(existing, subj.Json);
                        if (existing is IConfigurableSubject configurable)
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
                            newChildren[subj.Name] = newSubject;
                        }
                    }
                    break;
            }
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
            return Convert.ToHexStringLower(hashBuffer.Slice(0, 8));
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
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
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

    // Parsed segment types - internal for testing
    internal abstract record ParsedSegment;
    internal sealed record HtmlParsedSegment(string Html) : ParsedSegment;
    internal sealed record ExpressionParsedSegment(string Path) : ParsedSegment;
    internal sealed record SubjectParsedSegment(string Name, string Json) : ParsedSegment;
}

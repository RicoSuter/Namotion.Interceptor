using System.Net;
using System.Text.RegularExpressions;
using Markdig.Extensions.CustomContainers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

public abstract record MarkdownSegment;

public sealed record HtmlMarkdownSegment(string Html) : MarkdownSegment;

public sealed record ExpressionMarkdownSegment(string Path) : MarkdownSegment;

public sealed record BlockMarkdownSegment(MarkdownBlock Block, IReadOnlyList<MarkdownSegment> Content) : MarkdownSegment;

public static class MarkdownErrorBox
{
    public static string Render(int line, string message) =>
        $"<div class=\"markdown-block-error\" role=\"alert\">Line {line}: {WebUtility.HtmlEncode(message)}</div>\n";
}

/// <summary>Renders a parsed page to HTML and cuts it into segments per block.</summary>
public static partial class MarkdownSegmentBuilder
{
    public const string ExpressionPattern = @"\{\{\s*([^}]+?)\s*\}\}";

    [GeneratedRegex(@"<!--(/?)BLOCK:([0-9a-f]{32}):(\d+)-->|" + ExpressionPattern)]
    private static partial Regex MarkerRegex();

    public static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter = null)
    {
        // Page text may contain marker-shaped comments, so only markers carrying this token count.
        var token = Guid.NewGuid().ToString("N");
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = linkRewriter };
        MarkdownPageParser.Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<HtmlCustomContainerRenderer>(new BlockMarkerRenderer(page, token));

        // Extensions configure the code block renderer by exact type during Setup, so the
        // replacement is put in afterwards and takes over what they configured.
        var codeBlockRenderer = renderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (codeBlockRenderer is not null)
        {
            var legacyRenderer = new LegacySubjectFenceRenderer
            {
                OutputAttributesOnPre = codeBlockRenderer.OutputAttributesOnPre
            };
            legacyRenderer.BlocksAsDiv.UnionWith(codeBlockRenderer.BlocksAsDiv);
            foreach (var (language, tag) in codeBlockRenderer.BlockMapping)
            {
                legacyRenderer.BlockMapping[language] = tag;
            }

            legacyRenderer.TryWriters.AddRange(codeBlockRenderer.TryWriters);
            renderer.ObjectRenderers.Replace<CodeBlockRenderer>(legacyRenderer);
        }
        renderer.Render(page.Document);
        writer.Flush();

        return Split(writer.ToString(), page, token);
    }

    private static List<MarkdownSegment> Split(string html, MarkdownPage page, string token)
    {
        var root = new List<MarkdownSegment>();
        var stack = new Stack<(MarkdownBlock Block, List<MarkdownSegment> Outer)>();
        var current = root;
        var lastIndex = 0;

        foreach (Match match in MarkerRegex().Matches(html))
        {
            var isForgedMarker = match.Groups[2].Success && match.Groups[2].Value != token;
            if (isForgedMarker)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                current.Add(new HtmlMarkdownSegment(html[lastIndex..match.Index]));
            }

            lastIndex = match.Index + match.Length;

            if (match.Groups[4].Success)
            {
                current.Add(new ExpressionMarkdownSegment(match.Groups[4].Value));
            }
            else if (match.Groups[1].Length == 0)
            {
                stack.Push((page.AllBlocks[int.Parse(match.Groups[3].Value)], current));
                current = [];
            }
            else if (stack.Count > 0)
            {
                var (block, outer) = stack.Pop();
                outer.Add(new BlockMarkdownSegment(block, current));
                current = outer;
            }
        }

        if (lastIndex < html.Length)
        {
            current.Add(new HtmlMarkdownSegment(html[lastIndex..]));
        }

        return root;
    }

    private sealed class BlockMarkerRenderer(MarkdownPage page, string token) : HtmlObjectRenderer<CustomContainer>
    {
        private readonly HtmlCustomContainerRenderer _styleRenderer = new();

        protected override void Write(HtmlRenderer renderer, CustomContainer container)
        {
            var block = page.GetBlock(container);
            switch (block.Kind)
            {
                case MarkdownBlockKind.Style:
                    _styleRenderer.Write(renderer, container);
                    break;

                case MarkdownBlockKind.Invalid:
                    renderer.EnsureLine();
                    renderer.Write(MarkdownErrorBox.Render(block.Line, block.Error!));
                    break;

                default:
                    renderer.EnsureLine();
                    renderer.Write($"<!--BLOCK:{token}:{block.Index}-->");
                    for (var i = 0; i < container.Count; i++)
                    {
                        if (i == 0 && block.ConfigurationSpan is not null)
                        {
                            continue;
                        }

                        renderer.Render(container[i]);
                    }

                    renderer.Write($"<!--/BLOCK:{token}:{block.Index}-->");
                    break;
            }
        }
    }

    private sealed class LegacySubjectFenceRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            if (block is FencedCodeBlock { Info: { } info } && info.StartsWith("subject(", StringComparison.Ordinal))
            {
                renderer.EnsureLine();
                renderer.Write(MarkdownErrorBox.Render(
                    block.Line + 1,
                    "The subject(name) fence is no longer supported. Write the subject as a block: a line with :::Full.Type.Name#name key=value, then a closing ::: line."));
            }
            else
            {
                base.Write(renderer, block);
            }
        }
    }
}

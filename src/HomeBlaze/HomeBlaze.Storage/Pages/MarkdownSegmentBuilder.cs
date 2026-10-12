using System.Net;
using System.Text.RegularExpressions;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Pages;

public abstract record MarkdownSegment;

public sealed record HtmlMarkdownSegment(string Html) : MarkdownSegment;

public sealed record ExpressionMarkdownSegment(string Path, ExpressionFormat Format, string? Error) : MarkdownSegment;

public sealed record BlockMarkdownSegment(MarkdownBlock Block, IReadOnlyList<MarkdownSegment> Content) : MarkdownSegment;

/// <summary>One {{ path }} expression in the text of a page: where it stands, the path between the braces without the format, the format, and what is wrong with the format.</summary>
public readonly record struct MarkdownExpression(MarkdownSpan Span, string Path, ExpressionFormat Format, string? Error);

public static class MarkdownErrorBox
{
    public static string Render(int line, string message) =>
        $"<div class=\"markdown-block-error\" role=\"alert\">Line {line}: {WebUtility.HtmlEncode(message)}</div>\n";
}

/// <summary>Renders a parsed page to HTML and cuts it into segments per block.</summary>
public static partial class MarkdownSegmentBuilder
{
    // The path takes no brace and no '<'. Without that, an opening pair of braces that is never closed would
    // take everything up to the closing pair of a later expression, and in the rendered HTML also the
    // markers of the blocks in between.
    private const string ExpressionPattern = @"\{\{\s*([^{}<]+?)\s*\}\}";

    [GeneratedRegex(@"<!--(/?)BLOCK:([0-9a-f]{32}):(\d+)-->|" + ExpressionPattern)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    private static readonly char[] Braces = ['{', '}'];

    private static readonly string OldSubjectFenceError =
        "The subject(name) fence is no longer supported. Write the subject as a block: a line with " +
        $"{MarkdownBlockFence.Create(MarkdownBlockFence.MinimumLength)} Full.Type.Name#name key=value, " +
        $"then a closing {MarkdownBlockFence.Create(MarkdownBlockFence.MinimumLength)} line.";

    // The characters that the path part of the expression pattern excludes.
    private static readonly char[] ExcludedFromPath = ['{', '}', '<'];

    /// <summary>
    /// Gets the expressions in the text of the page in document order. An expression is a path between two
    /// pairs of braces that contains no brace and no '&lt;'. A brace inside markdown code neither opens nor
    /// closes one.
    /// </summary>
    /// <remarks>
    /// The search reads the text and not the rendered page, so it also returns an expression that
    /// <see cref="Build"/> does not turn into a segment: one in the front matter, in the content of an invalid
    /// block or on the head line of a block, and one whose braces enclose markdown markup or the border of a block.
    /// A path with a character reference in it, such as <c>&amp;amp;</c>, is returned as written, while the
    /// segment has the character.
    /// </remarks>
    public static IEnumerable<MarkdownExpression> FindExpressions(MarkdownPage page)
    {
        var source = page.Source;
        foreach (Match match in ExpressionRegex().Matches(HideBracesInCode(page)))
        {
            var path = match.Groups[1];
            var text = MarkdownExpressionText.Parse(source.Substring(path.Index, path.Length));
            yield return new MarkdownExpression(
                new MarkdownSpan(match.Index, match.Length), text.Path, text.Format, text.Error);
        }
    }

    /// <summary>Gets the text of the page with every brace inside markdown code replaced. All offsets stay the same.</summary>
    private static string HideBracesInCode(MarkdownPage page)
    {
        var source = page.Source;
        char[]? hidden = null;
        for (var index = source.IndexOfAny(Braces); index >= 0; index = source.IndexOfAny(Braces, index + 1))
        {
            if (page.IsInsideCode(index))
            {
                hidden ??= source.ToCharArray();
                hidden[index] = '\0';
            }
        }

        return hidden is null ? source : new string(hidden);
    }

    public static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter = null)
    {
        // Page text may contain marker-shaped comments, so only markers carrying this token count.
        var token = Guid.NewGuid().ToString("N");
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = linkRewriter };
        MarkdownPageParser.Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new BlockMarkerRenderer(page, token));

        // Extensions configure the code renderers by exact type during Setup, so the
        // replacements are put in afterwards and take over what they configured.
        var literalCode = new LiteralCodeWriter();
        var codeBlockRenderer = renderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (codeBlockRenderer is not null)
        {
            var literalRenderer = new LiteralCodeBlockRenderer(literalCode)
            {
                OutputAttributesOnPre = codeBlockRenderer.OutputAttributesOnPre
            };
            literalRenderer.BlocksAsDiv.UnionWith(codeBlockRenderer.BlocksAsDiv);
            foreach (var (language, tag) in codeBlockRenderer.BlockMapping)
            {
                literalRenderer.BlockMapping[language] = tag;
            }

            literalRenderer.TryWriters.AddRange(codeBlockRenderer.TryWriters);
            renderer.ObjectRenderers.Replace<CodeBlockRenderer>(literalRenderer);
        }

        var codeInlineRenderer = renderer.ObjectRenderers.FindExact<CodeInlineRenderer>();
        if (codeInlineRenderer is not null)
        {
            var literalRenderer = new LiteralCodeInlineRenderer(literalCode);
            literalRenderer.TryWriters.AddRange(codeInlineRenderer.TryWriters);
            renderer.ObjectRenderers.Replace<CodeInlineRenderer>(literalRenderer);
        }

        renderer.Render(page.Document);
        writer.Flush();

        return Split(writer.ToString(), page, token);
    }

    internal static List<MarkdownSegment> Split(string html, MarkdownPage page, string token)
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

            // The path is HTML here, so a quote or an ampersand in it is a character reference.
            var path = match.Groups[4].Success ? WebUtility.HtmlDecode(match.Groups[4].Value) : null;

            // The pattern cannot see a '<' or a brace that the page text has literally, because the HTML has
            // a character reference for it. In the text such braces enclose no expression, so they stay text here.
            if (path is not null && path.AsSpan().IndexOfAny(ExcludedFromPath) >= 0)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                current.Add(new HtmlMarkdownSegment(html[lastIndex..match.Index]));
            }

            lastIndex = match.Index + match.Length;

            if (path is not null)
            {
                var text = MarkdownExpressionText.Parse(path);
                current.Add(new ExpressionMarkdownSegment(text.Path, text.Format, text.Error));
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

        // A closing marker that was not found must not take the content of its block out of the result.
        while (stack.Count > 0)
        {
            var (block, outer) = stack.Pop();
            outer.Add(new BlockMarkdownSegment(block, current));
            current = outer;
        }

        return root;
    }

    private sealed class BlockMarkerRenderer(MarkdownPage page, string token) : HtmlObjectRenderer<MarkdownBlockContainer>
    {
        protected override void Write(HtmlRenderer renderer, MarkdownBlockContainer container)
        {
            var block = page.GetBlock(container);
            switch (block.Kind)
            {
                case MarkdownBlockKind.Style:
                    renderer.EnsureLine();
                    renderer.Write("<div").WriteAttributes(container).Write('>');
                    renderer.WriteChildren(container);
                    renderer.WriteLine("</div>");
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

    /// <summary>
    /// Collects what a code renderer writes and passes it on with every brace as a character reference.
    /// A browser shows the braces, and the expression pattern does not match them.
    /// </summary>
    private sealed class LiteralCodeWriter
    {
        private readonly StringWriter _code = new();

        public TextWriter Begin(HtmlRenderer renderer)
        {
            var output = renderer.Writer;
            renderer.Writer = _code;
            return output;
        }

        public void End(HtmlRenderer renderer, TextWriter output)
        {
            renderer.Writer = output;

            var code = _code.GetStringBuilder();
            code.Replace("{", "&#123;").Replace("}", "&#125;");
            foreach (var chunk in code.GetChunks())
            {
                output.Write(chunk.Span);
            }

            code.Clear();
        }
    }

    private sealed class LiteralCodeBlockRenderer(LiteralCodeWriter literalCode) : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            if (block is FencedCodeBlock { Info: { } info } && info.StartsWith("subject(", StringComparison.Ordinal))
            {
                renderer.EnsureLine();
                renderer.Write(MarkdownErrorBox.Render(block.Line + 1, OldSubjectFenceError));
            }
            else
            {
                var output = literalCode.Begin(renderer);
                base.Write(renderer, block);
                literalCode.End(renderer, output);
            }
        }
    }

    private sealed class LiteralCodeInlineRenderer(LiteralCodeWriter literalCode) : CodeInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeInline code)
        {
            var output = literalCode.Begin(renderer);
            base.Write(renderer, code);
            literalCode.End(renderer, output);
        }
    }
}

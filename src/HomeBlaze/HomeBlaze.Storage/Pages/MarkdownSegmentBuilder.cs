using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Pages;

public abstract record MarkdownSegment;

/// <param name="Html">The HTML of the segment.</param>
/// <param name="BlockStarts">
/// The offsets into <paramref name="Html"/>, in ascending order, at which a top-level block of the page starts, so that the
/// HTML from there on does not depend on the HTML before it. An offset of <c>Html.Length</c> means that a block starts
/// after the segment, and an offset of 0 that one starts after the expression before it. Default when there is none.
/// </param>
public sealed record HtmlMarkdownSegment(string Html, ImmutableArray<int> BlockStarts = default) : MarkdownSegment
{
    public ImmutableArray<int> BlockStarts { get; } = BlockStarts.IsDefaultOrEmpty ? default : BlockStarts;

    // By content, because the equality of an immutable array compares the array it wraps.
    public bool Equals(HtmlMarkdownSegment? other) =>
        other is not null && Html == other.Html && BlockStarts.AsSpan().SequenceEqual(other.BlockStarts.AsSpan());

    public override int GetHashCode() => HashCode.Combine(Html, BlockStarts.IsDefault ? 0 : BlockStarts.Length);
}

public sealed record ExpressionMarkdownSegment(string Text) : MarkdownSegment;

public sealed record BlockMarkdownSegment(MarkdownBlock Block, IReadOnlyList<MarkdownSegment> Content) : MarkdownSegment;

/// <summary>One {{ … }} expression in the text of a page: where it stands, and the text between the braces without the white space around it.</summary>
public readonly record struct MarkdownExpression(MarkdownSpan Span, string Text);

public static class MarkdownErrorBox
{
    public static string Render(int line, string message) =>
        $"<div class=\"markdown-block-error\" role=\"alert\">Line {line}: {WebUtility.HtmlEncode(message)}</div>\n";
}

/// <summary>Renders a parsed page to HTML and cuts it into segments per block.</summary>
public static partial class MarkdownSegmentBuilder
{
    // The text takes no brace and no '<'. Without that, an opening pair of braces that is never closed would take
    // everything up to the closing pair of a later expression, and in the rendered HTML also the markers in between.
    // It finds the expressions in HTML that the page writes; those in running text are markers already.
    private const string ExpressionPattern = @"\{\{\s*([^{}<]+?)\s*\}\}";

    // The source is searched with '<' allowed, because running text has the operators of an expression as written.
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex SourceExpressionRegex();

    [GeneratedRegex(@"<!--(/?)BLOCK:([0-9a-f]{32}):(\d+)-->|<!--EXPR:([0-9a-f]{32}):(\d+)-->|" + ExpressionPattern + @"|<!--BREAK:([0-9a-f]{32})-->")]
    private static partial Regex MarkerRegex();

    private static readonly char[] Braces = ['{', '}'];

    // A comment, whose content is no tag, or a tag: its name, and a slash that closes it when it ends with one.
    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)|<(/?)([A-Za-z][A-Za-z0-9-]*)(?:[^>""']|""[^""]*""|'[^']*')*?(/?)>")]
    private static partial Regex HtmlTagRegex();

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
    };

    private static readonly string OldSubjectFenceError =
        "The subject(name) fence is no longer supported. Write the subject as a block: a line with " +
        $"{MarkdownBlockFence.Create(MarkdownBlockFence.MinimumLength)} Full.Type.Name#name key=value, " +
        $"then a closing {MarkdownBlockFence.Create(MarkdownBlockFence.MinimumLength)} line.";

    // A brace that the HTML has as a character reference comes from markdown code, and encloses no expression.
    private static readonly char[] ExcludedFromText = ['{', '}'];

    /// <summary>
    /// Gets the expressions in the text of the page in document order: the text between two pairs of braces that
    /// contains no brace. A brace inside markdown code neither opens nor closes one. The head line of a block and its json block are skipped.
    /// </summary>
    /// <remarks>
    /// The search reads the text and not the rendered page, so it also returns an expression that
    /// <see cref="Build"/> does not turn into a segment: one in the front matter, in the content of an invalid
    /// block, and one whose braces enclose markdown markup or the border of a block.
    /// An expression with a character reference in it, such as <c>&amp;amp;</c>, is returned as written, except one
    /// in HTML that the page writes: its text has the character, like its segment, and one whose character
    /// is a brace is skipped. The segment of an expression in running text keeps it as written.
    /// </remarks>
    public static IEnumerable<MarkdownExpression> FindExpressions(MarkdownPage page)
    {
        var source = page.Source;
        foreach (Match match in SourceExpressionRegex().Matches(HideBracesInCode(page)))
        {
            if (IsInBlockArguments(page, match.Index))
            {
                continue;
            }

            var group = match.Groups[1];
            var text = source.Substring(group.Index, group.Length);

            // Raw HTML is HTML to the page, which reads a character reference in it, so a '<' is written as &lt;.
            if (page.IsInsideRawHtml(match.Index))
            {
                text = WebUtility.HtmlDecode(text);
                if (text.AsSpan().IndexOfAny(ExcludedFromText) >= 0)
                {
                    continue;
                }
            }

            yield return new MarkdownExpression(new MarkdownSpan(match.Index, match.Length), text);
        }
    }

    // The head line and the json block of a block hold its arguments, whose expressions the widget evaluates itself.
    private static bool IsInBlockArguments(MarkdownPage page, int position)
    {
        foreach (var block in page.AllBlocks)
        {
            if (IsInside(block.HeadSpan, position) || (block.ConfigurationSpan is { } configuration && IsInside(configuration, position)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(MarkdownSpan span, int position) => position >= span.Start && position < span.End;

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

    /// <summary>Gets whether a block is a fenced code block of the old <c>subject(name)</c> form, which is shown as an error.</summary>
    internal static bool IsOldSubjectFence(Block block) =>
        block is FencedCodeBlock { Info: { } info } && info.StartsWith("subject(", StringComparison.Ordinal);

    public static IReadOnlyList<MarkdownSegment> Build(MarkdownPage page, Func<string, string>? linkRewriter = null)
    {
        // Page text may contain marker-shaped comments, so only markers carrying this token count.
        var token = Guid.NewGuid().ToString("N");
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer) { LinkRewriter = linkRewriter };
        MarkdownPageParser.Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new BlockMarkerRenderer(page, token));
        var expressions = new List<string>();
        renderer.ObjectRenderers.Insert(0, new ExpressionMarkerRenderer(token, expressions));

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

        var tableRenderer = renderer.ObjectRenderers.FindExact<HtmlTableRenderer>();
        if (tableRenderer is not null)
        {
            var scrollableRenderer = new ScrollableTableRenderer();
            scrollableRenderer.TryWriters.AddRange(tableRenderer.TryWriters);
            renderer.ObjectRenderers.Replace<HtmlTableRenderer>(scrollableRenderer);
        }

        WriteBreaksBetweenBlocks(renderer, page.Document, writer, token);
        renderer.Render(page.Document);
        writer.Flush();

        return Split(writer.ToString(), page, token, expressions);
    }

    /// <summary>Writes a break marker before each top-level block of the document that may start a markup string of its own.</summary>
    private static void WriteBreaksBetweenBlocks(HtmlRenderer renderer, MarkdownDocument document, StringWriter writer, string token)
    {
        // Blazor parses each markup string on its own, so a tag opened in one block and closed in a later one would be
        // closed early. No break is written between the first and the last top-level block that contains raw HTML.
        // A tag that is still open after the last of them reaches to the end of the page.
        var firstRawHtml = -1;
        var lastRawHtml = -1;
        Dictionary<string, int>? openTags = null;
        for (var index = 0; index < document.Count; index++)
        {
            foreach (var rawHtml in GetRawHtml(document[index]))
            {
                firstRawHtml = firstRawHtml < 0 ? index : firstRawHtml;
                lastRawHtml = index;
                CountOpenTags(rawHtml, openTags ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            }
        }

        if (openTags is not null && openTags.Values.Any(count => count > 0))
        {
            lastRawHtml = document.Count - 1;
        }

        var marker = $"<!--BREAK:{token}-->";
        var blockIndex = 0;
        renderer.ObjectWriteBefore += (_, markdownObject) =>
        {
            if (markdownObject is not Block block || !ReferenceEquals(block.Parent, document))
            {
                return;
            }

            while (blockIndex < document.Count && !ReferenceEquals(document[blockIndex], block))
            {
                blockIndex++;
            }

            // Hooked into the rendering of the whole document, so that each block renders as it does there. The marker
            // is written past the renderer, which then still knows whether the HTML before it ended a line.
            var isOutsideRawHtml = lastRawHtml < 0 || blockIndex <= firstRawHtml || blockIndex > lastRawHtml;
            if (isOutsideRawHtml && writer.GetStringBuilder().Length > 0)
            {
                writer.Write(marker);
            }
        };
    }

    /// <summary>Gets the raw HTML that a block and the blocks and inlines inside it write, in document order.</summary>
    private static IEnumerable<string> GetRawHtml(Block block)
    {
        IEnumerable<MarkdownObject> descendants = block switch
        {
            HtmlBlock => [block],
            LeafBlock { Inline: { } inline } => inline.Descendants<HtmlInline>(),
            ContainerBlock container => container.Descendants(),
            _ => []
        };

        foreach (var descendant in descendants)
        {
            if (descendant is HtmlBlock htmlBlock)
            {
                yield return htmlBlock.Lines.ToString();
            }
            else if (descendant is HtmlInline htmlInline)
            {
                yield return htmlInline.Tag;
            }
        }
    }

    private static void CountOpenTags(string rawHtml, Dictionary<string, int> openTags)
    {
        foreach (Match match in HtmlTagRegex().Matches(rawHtml))
        {
            var name = match.Groups[2];
            if (!name.Success || match.Groups[3].Length > 0 || VoidElements.Contains(name.Value))
            {
                continue;
            }

            // A closing tag without an opening one before it closes nothing, also not one that is opened later.
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(openTags, name.Value, out _);
            count = match.Groups[1].Length == 0 ? count + 1 : Math.Max(0, count - 1);
        }
    }

    internal static List<MarkdownSegment> Split(string html, MarkdownPage page, string token, IReadOnlyList<string> expressions)
    {
        var root = new List<MarkdownSegment>();
        var stack = new Stack<(MarkdownBlock Block, List<MarkdownSegment> Outer)>();
        var current = root;
        var lastIndex = 0;

        // A break marker does not end a segment: the HTML around it is one run, and the break is kept as an offset into it.
        var run = new StringBuilder();
        var blockStarts = new List<int>();
        var hasBreak = false;

        foreach (Match match in MarkerRegex().Matches(html))
        {
            var isForgedMarker =
                (match.Groups[2].Success && match.Groups[2].Value != token) ||
                (match.Groups[4].Success && match.Groups[4].Value != token) ||
                (match.Groups[7].Success && match.Groups[7].Value != token);
            if (isForgedMarker)
            {
                continue;
            }

            // An expression in HTML that the page writes is HTML, so a quote or an ampersand in it is a character reference.
            var expression = match.Groups[4].Success
                ? expressions[int.Parse(match.Groups[5].Value)]
                : match.Groups[6].Success ? WebUtility.HtmlDecode(match.Groups[6].Value) : null;

            if (match.Groups[6].Success && expression!.AsSpan().IndexOfAny(ExcludedFromText) >= 0)
            {
                continue;
            }

            AppendToRun(html, lastIndex, match.Index, current, run, blockStarts, ref hasBreak);
            lastIndex = match.Index + match.Length;

            if (match.Groups[7].Success)
            {
                hasBreak = true;
                continue;
            }

            // Before a widget nothing is kept: a widget ends the markup string anyway.
            if (hasBreak && expression is not null && run.Length > 0)
            {
                blockStarts.Add(run.Length);
            }

            hasBreak = false;
            EndRun(current, run, blockStarts);

            if (expression is not null)
            {
                current.Add(new ExpressionMarkdownSegment(expression));
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

        AppendToRun(html, lastIndex, html.Length, current, run, blockStarts, ref hasBreak);
        EndRun(current, run, blockStarts);

        // A closing marker that was not found must not take the content of its block out of the result.
        while (stack.Count > 0)
        {
            var (block, outer) = stack.Pop();
            outer.Add(new BlockMarkdownSegment(block, current));
            current = outer;
        }

        return root;
    }

    private static void AppendToRun(
        string html, int start, int end, List<MarkdownSegment> current, StringBuilder run, List<int> blockStarts, ref bool hasBreak)
    {
        if (end <= start)
        {
            return;
        }

        // At the start of a run, a block start matters only after an expression, whose text the view joins with the HTML.
        if (hasBreak && (run.Length > 0 || current is [.., ExpressionMarkdownSegment]))
        {
            blockStarts.Add(run.Length);
        }

        hasBreak = false;
        run.Append(html, start, end - start);
    }

    private static void EndRun(List<MarkdownSegment> current, StringBuilder run, List<int> blockStarts)
    {
        if (run.Length == 0)
        {
            return;
        }

        current.Add(new HtmlMarkdownSegment(run.ToString(), blockStarts.Count > 0 ? [.. blockStarts] : default));
        run.Clear();
        blockStarts.Clear();
    }

    // The marker holds an index and not the text, so that no text of an expression, such as "-->", can end the comment.
    private sealed class ExpressionMarkerRenderer(string token, List<string> expressions) : HtmlObjectRenderer<MarkdownExpressionInline>
    {
        protected override void Write(HtmlRenderer renderer, MarkdownExpressionInline expression)
        {
            renderer.Write($"<!--EXPR:{token}:{expressions.Count}-->");
            expressions.Add(expression.Text);
        }
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

        // With breakAfterDots the content may break after each dot, so that a type name in a narrow table column breaks
        // between its segments and not inside one.
        public void End(HtmlRenderer renderer, TextWriter output, bool breakAfterDots = false)
        {
            renderer.Writer = output;

            var code = _code.GetStringBuilder();
            code.Replace("{", "&#123;").Replace("}", "&#125;");
            if (breakAfterDots)
            {
                // Between the end of the opening tag and the closing tag, so that no attribute changes.
                var text = code.ToString();
                var contentStart = text.IndexOf('>') + 1;
                var contentEnd = text.LastIndexOf('<');
                if (contentStart > 0 && contentEnd > contentStart)
                {
                    code.Replace(".", ".<wbr>", contentStart, contentEnd - contentStart);
                }
            }

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
            if (IsOldSubjectFence(block))
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

    // The box, and not the table, scrolls sideways (see MarkdownFilePageComponent.razor.css of HomeBlaze.Storage.Blazor):
    // a table whose display is changed loses its table semantics in some browsers.
    private sealed class ScrollableTableRenderer : HtmlTableRenderer
    {
        protected override void Write(HtmlRenderer renderer, Table table)
        {
            renderer.EnsureLine();
            renderer.WriteLine("<div class=\"markdown-table\">");
            base.Write(renderer, table);
            renderer.EnsureLine();
            renderer.WriteLine("</div>");
        }
    }

    private sealed class LiteralCodeInlineRenderer(LiteralCodeWriter literalCode) : CodeInlineRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeInline code)
        {
            var output = literalCode.Begin(renderer);
            base.Write(renderer, code);
            literalCode.End(renderer, output, breakAfterDots: IsInTableCell(code));
        }

        private static bool IsInTableCell(Inline inline)
        {
            var container = inline.Parent;
            while (container?.Parent is not null)
            {
                container = container.Parent;
            }

            return container?.ParentBlock?.Parent is TableCell;
        }
    }
}

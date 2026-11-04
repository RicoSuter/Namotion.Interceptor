using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Pages;

/// <summary>An expression in running text, with the text between its braces as the page has it, without the white space around it.</summary>
internal sealed class MarkdownExpressionInline : LeafInline
{
    public required string Text { get; init; }
}

/// <summary>
/// Reads <c>{{ … }}</c> in running text before the other inline parsers, so that the operators of an expression reach it
/// as written: no emphasis, no highlight of <c>==</c>, and no cell border at a <c>|</c> in a table.
/// </summary>
internal sealed class MarkdownExpressionInlineParser : InlineParser
{
    public MarkdownExpressionInlineParser()
    {
        OpeningCharacters = ['{'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        if (slice.PeekCharExtra(1) != '{')
        {
            return false;
        }

        // An expression takes no brace, so one that is never closed does not take the text up to a later expression.
        // It takes no backtick either: this parser runs before the one of code spans, so a span that starts inside
        // the braces would otherwise never be parsed, and its closing braces would end the expression.
        var text = slice.Text;
        var start = slice.Start;
        var end = start + 2;
        while (end <= slice.End && text[end] is not ('{' or '}' or '`'))
        {
            end++;
        }

        if (end >= slice.End || text[end] != '}' || text[end + 1] != '}')
        {
            return false;
        }

        var inner = text.AsSpan(start + 2, end - start - 2).Trim();
        if (inner.IsEmpty)
        {
            return false;
        }

        var sourceStart = processor.GetSourcePosition(start, out var line, out var column);
        processor.Inline = new MarkdownExpressionInline
        {
            Text = inner.ToString(),
            Span = new SourceSpan(sourceStart, sourceStart + end + 1 - start),
            Line = line,
            Column = column
        };
        slice.Start = end + 2;
        return true;
    }
}

/// <summary>Writes an expression back as written, for a renderer that does not cut the page into segments.</summary>
internal sealed class MarkdownExpressionInlineRenderer : HtmlObjectRenderer<MarkdownExpressionInline>
{
    protected override void Write(HtmlRenderer renderer, MarkdownExpressionInline expression)
    {
        renderer.Write("{{ ").WriteEscape(expression.Text).Write(" }}");
    }
}

/// <summary>Adds the reading of expressions in running text to a pipeline.</summary>
internal sealed class MarkdownExpressionExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<MarkdownExpressionInlineParser>())
        {
            // First, so that it runs before the parser of attributes in braces.
            pipeline.InlineParsers.Insert(0, new MarkdownExpressionInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer && !htmlRenderer.ObjectRenderers.Contains<MarkdownExpressionInlineRenderer>())
        {
            htmlRenderer.ObjectRenderers.Add(new MarkdownExpressionInlineRenderer());
        }
    }
}

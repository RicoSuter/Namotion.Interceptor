using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// A top-level piece of the content of a page, or of a widget or slot block, that is not a block: a Markdig block of text,
/// such as a paragraph, a heading, a list or a table, or other content, such as raw HTML, a style block or an error.
/// </summary>
/// <param name="FirstBlock">The Markdig block that it starts with.</param>
/// <param name="Line">Its first 1-based line.</param>
/// <param name="EndLine">Its last 1-based line that is not empty.</param>
/// <param name="Owner">The block whose content it is, or null for the page.</param>
/// <param name="IsEditable">
/// Whether it can be edited as text: false for a run of content with raw HTML, a style block, an error, the footnotes, an
/// old subject fence and a Markdig container with a block inside.
/// </param>
internal sealed record MarkdownTextBlock(Block FirstBlock, int Line, int EndLine, MarkdownBlock? Owner, bool IsEditable);

/// <summary>Finds the text blocks of a page, see <see cref="MarkdownTextBlock"/>.</summary>
internal static class MarkdownTextBlocks
{
    /// <summary>
    /// Gets the text blocks of the page and of every widget and slot block, ordered by line. Content that renders nothing,
    /// such as empty lines, link reference definitions and the front matter, has none.
    /// </summary>
    public static List<MarkdownTextBlock> Find(MarkdownPage page)
    {
        var result = new List<MarkdownTextBlock>();
        AddContent(page, page.Document, owner: null, result);
        Visit(page, page.Document, result);
        result.Sort(static (first, second) => first.Line.CompareTo(second.Line));
        return result;
    }

    // Finds the widget and slot blocks at every depth, also inside a list or a style block, and adds their content.
    private static void Visit(MarkdownPage page, ContainerBlock container, List<MarkdownTextBlock> result)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer blockContainer &&
                page.GetBlock(blockContainer) is { Kind: MarkdownBlockKind.Widget or MarkdownBlockKind.Slot } block)
            {
                AddContent(page, blockContainer, block, result);
            }

            if (child is ContainerBlock inner)
            {
                Visit(page, inner, result);
            }
        }
    }

    private static void AddContent(MarkdownPage page, ContainerBlock container, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        var run = new List<Block>();

        // The json block of a block is its configuration and is not rendered, as in MarkdownSegmentBuilder.
        var first = owner?.ConfigurationSpan is not null ? 1 : 0;
        for (var index = first; index < container.Count; index++)
        {
            var child = container[index];
            if (child is MarkdownBlockContainer blockContainer &&
                page.GetBlock(blockContainer).Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot)
            {
                AddRun(page, run, owner, result);
                run.Clear();
            }
            else if (child is not (BlankLineBlock or LinkReferenceDefinitionGroup or YamlFrontMatterBlock))
            {
                run.Add(child);
            }
        }

        AddRun(page, run, owner, result);
    }

    // Raw HTML can open an element in one block and close it in a later one, so a run with raw HTML stays one piece.
    private static void AddRun(MarkdownPage page, List<Block> run, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        if (run.Count == 0)
        {
            return;
        }

        if (run.Exists(static block => block is HtmlBlock))
        {
            result.Add(new MarkdownTextBlock(run[0], GetLine(run[0]), GetEndLine(page, run[^1]), owner, IsEditable: false));
            return;
        }

        foreach (var block in run)
        {
            result.Add(new MarkdownTextBlock(block, GetLine(block), GetEndLine(page, block), owner, IsEditable(page, block)));
        }
    }

    private static bool IsEditable(MarkdownPage page, Block block) =>
        block is not (MarkdownBlockContainer or FootnoteGroup) &&
        !MarkdownSegmentBuilder.IsOldSubjectFence(block) &&
        !(block is ContainerBlock container && HoldsBlock(container));

    private static bool HoldsBlock(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer || (child is ContainerBlock inner && HoldsBlock(inner)))
            {
                return true;
            }
        }

        return false;
    }

    // The group of the footnotes has no line of its own, so its footnotes give its lines.
    private static int GetLine(Block block) =>
        (block is FootnoteGroup { Count: > 0 } group ? group[0] : block).Line + 1;

    // The line of the last character that is not white space: the span of an indented code block takes the empty lines
    // after it, and a block of the page knows its own last line, also without a closing line.
    private static int GetEndLine(MarkdownPage page, Block block)
    {
        if (block is FootnoteGroup { Count: > 0 } group)
        {
            block = group[group.Count - 1];
        }

        if (block is MarkdownBlockContainer blockContainer)
        {
            return page.GetBlock(blockContainer).EndLine;
        }

        var source = page.Source;
        var end = Math.Clamp(block is CodeBlock code ? MarkdownPageParser.GetCodeBlockEnd(source, code) : block.Span.End + 1, 0, source.Length);
        var start = Math.Clamp(block.Span.Start, 0, end);
        var lastCharacter = start + source.AsSpan(start, end - start).TrimEnd().Length - 1;
        return source.AsSpan(0, Math.Max(lastCharacter, 0)).Count('\n') + 1;
    }
}

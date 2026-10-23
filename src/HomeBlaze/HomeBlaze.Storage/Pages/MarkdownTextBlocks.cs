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
/// Whether it can be edited as text: false for raw HTML, a style block, an error, the footnotes, an old subject fence, a
/// Markdig container with a block inside, and every text block of a page with a line ending that is a carriage return alone.
/// </param>
/// <param name="HoldsHtml">
/// Whether it is a block of raw HTML, together with the blocks up to the raw HTML that closes an element which it leaves open,
/// or up to the end of the run of content between two widget or slot blocks when none does. Raw HTML can take the text that
/// comes next to it.
/// </param>
internal sealed record MarkdownTextBlock(Block FirstBlock, int Line, int EndLine, MarkdownBlock? Owner, bool IsEditable, bool HoldsHtml);

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

        // Markdig ends a line at a lone carriage return as well, but the writes count lines by line feeds, so the lines
        // of a text block are not its own there.
        if (MarkdownBlockWriter.HasLoneCarriageReturn(page.Source))
        {
            for (var index = 0; index < result.Count; index++)
            {
                result[index] = result[index] with { IsEditable = false };
            }
        }

        return result;
    }

    // Finds the widget and slot blocks at every depth, also inside a list or a style block, and adds their content. The
    // parser does not read the nodes inside an invalid block, which have no block of the page.
    private static void Visit(MarkdownPage page, ContainerBlock container, List<MarkdownTextBlock> result)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer blockContainer)
            {
                var block = page.GetBlock(blockContainer);
                if (block.Kind == MarkdownBlockKind.Invalid)
                {
                    continue;
                }

                if (block.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot)
                {
                    AddContent(page, blockContainer, block, result);
                }
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

    // Every block of the run is a text block of its own, and raw HTML as well, which cannot be edited as text. Raw HTML can
    // open an element in one block and close it in a later one, so a block that leaves an element open keeps the blocks up to
    // the one that closes it in its text block: rendered on their own, the element would end in the first one.
    private static void AddRun(MarkdownPage page, List<Block> run, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        List<string>? openElements = null;
        for (var index = 0; index < run.Count; index++)
        {
            var block = run[index];
            if (block is not HtmlBlock html)
            {
                result.Add(new MarkdownTextBlock(block, GetLine(block), GetEndLine(page, block), owner, IsEditable(page, block), HoldsHtml: false));
                continue;
            }

            openElements ??= [];
            openElements.Clear();
            ReadElements(page.Source, html, openElements);
            while (openElements.Count > 0 && index + 1 < run.Count)
            {
                if (run[++index] is HtmlBlock next)
                {
                    ReadElements(page.Source, next, openElements);
                }
            }

            result.Add(new MarkdownTextBlock(block, GetLine(block), GetEndLine(page, run[index]), owner, IsEditable: false, HoldsHtml: true));
        }
    }

    // Reads the tags of the raw HTML of the block into the names of the open elements, much as a browser does: a start tag
    // opens an element, and an end tag closes the last open element of its name together with the ones opened after it.
    // Comments, the other markup that starts with <! or <?, void elements, self-closing tags and the text of script, style,
    // textarea and title elements open none.
    private static void ReadElements(string source, HtmlBlock block, List<string> openElements)
    {
        var start = Math.Clamp(block.Span.Start, 0, source.Length);
        var html = source.AsSpan(start, Math.Clamp(block.Span.End + 1, start, source.Length) - start);
        var index = 0;
        while (index < html.Length)
        {
            var tagStart = html[index..].IndexOf('<');
            if (tagStart < 0)
            {
                break;
            }

            index += tagStart;
            var rest = html[index..];
            if (rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = rest[4..].IndexOf("-->", StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    break;
                }

                index += 4 + commentEnd + 3;
                continue;
            }

            // A declaration, a processing instruction or a CDATA section, which a browser reads as a comment up to the next '>'.
            if (rest.Length > 1 && rest[1] is '!' or '?')
            {
                var declarationEnd = rest.IndexOf('>');
                if (declarationEnd < 0)
                {
                    break;
                }

                index += declarationEnd + 1;
                continue;
            }

            var isEndTag = rest.Length > 1 && rest[1] == '/';
            var nameStart = isEndTag ? 2 : 1;
            var nameEnd = nameStart;
            while (nameEnd < rest.Length && (char.IsAsciiLetterOrDigit(rest[nameEnd]) || rest[nameEnd] == '-'))
            {
                nameEnd++;
            }

            if (nameEnd == nameStart || !char.IsAsciiLetter(rest[nameStart]))
            {
                index++;
                continue;
            }

            var name = rest[nameStart..nameEnd];
            var tagEnd = FindTagEnd(rest, nameEnd);
            if (tagEnd < 0)
            {
                break;
            }

            index += tagEnd + 1;
            if (IsVoidElement(name))
            {
                continue;
            }

            if (isEndTag)
            {
                for (var open = openElements.Count - 1; open >= 0; open--)
                {
                    if (name.Equals(openElements[open], StringComparison.OrdinalIgnoreCase))
                    {
                        openElements.RemoveRange(open, openElements.Count - open);
                        break;
                    }
                }
            }
            else if (rest[tagEnd - 1] != '/')
            {
                openElements.Add(name.ToString());
                if (IsRawTextElement(name))
                {
                    // Up to its end tag, which the loop reads next.
                    var endTag = FindEndTag(html[index..], name);
                    if (endTag < 0)
                    {
                        break;
                    }

                    index += endTag;
                }
            }
        }
    }

    // The offset of the '>' that ends the tag, past quoted attribute values, or -1.
    private static int FindTagEnd(ReadOnlySpan<char> tag, int offset)
    {
        for (var index = offset; index < tag.Length; index++)
        {
            if (tag[index] is '"' or '\'')
            {
                var quoteEnd = tag[(index + 1)..].IndexOf(tag[index]);
                if (quoteEnd < 0)
                {
                    return -1;
                }

                index += quoteEnd + 1;
            }
            else if (tag[index] == '>')
            {
                return index;
            }
        }

        return -1;
    }

    // The offset of the end tag of the element with the name, in any case, or -1.
    private static int FindEndTag(ReadOnlySpan<char> html, ReadOnlySpan<char> name)
    {
        for (var index = html.IndexOf("</", StringComparison.Ordinal); index >= 0;)
        {
            var candidate = html[(index + 2)..];
            if (candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                (candidate.Length == name.Length || !char.IsAsciiLetterOrDigit(candidate[name.Length])))
            {
                return index;
            }

            var next = candidate.IndexOf("</", StringComparison.Ordinal);
            index = next < 0 ? -1 : index + 2 + next;
        }

        return -1;
    }

    private static bool IsVoidElement(ReadOnlySpan<char> name)
    {
        foreach (var voidElement in (ReadOnlySpan<string>)["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"])
        {
            if (name.Equals(voidElement, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRawTextElement(ReadOnlySpan<char> name) =>
        name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("textarea", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("title", StringComparison.OrdinalIgnoreCase);

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

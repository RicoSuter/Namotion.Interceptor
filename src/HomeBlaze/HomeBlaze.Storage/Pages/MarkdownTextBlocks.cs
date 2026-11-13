using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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
/// <param name="LeavesHtmlOpen">
/// Whether it holds raw HTML that leaves an element or a construct, such as a comment, open up to the end of its run, which
/// takes text that goes after it in the run.
/// </param>
/// <param name="ShowsNothing">Whether it is raw HTML that shows nothing, such as a comment.</param>
internal sealed record MarkdownTextBlock(
    Block FirstBlock,
    int Line,
    int EndLine,
    MarkdownBlock? Owner,
    bool IsEditable,
    bool HoldsHtml,
    bool LeavesHtmlOpen = false,
    bool ShowsNothing = false);

/// <summary>Finds the text blocks of a page, see <see cref="MarkdownTextBlock"/>.</summary>
internal static class MarkdownTextBlocks
{
    /// <summary>
    /// Gets the text blocks of the page and of every widget and slot block, ordered by line. Content that renders nothing,
    /// such as empty lines, link reference definitions and the front matter, has none. The page keeps them.
    /// </summary>
    public static IReadOnlyList<MarkdownTextBlock> Find(MarkdownPage page) => page.GetTextBlocks(Create);

    private static MarkdownTextBlock[] Create(MarkdownPage page)
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

        return result.ToArray();
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

    // Every block of the run is a text block of its own, and raw HTML as well, which cannot be edited as text. HTML can open
    // an element in one block and close it in a later one, so a block that leaves an element open keeps the blocks up to the
    // one that closes it in its text block: rendered on their own, the element would end in the first one. A construct that
    // is not closed, such as a comment, keeps the rest of the run.
    private static void AddRun(MarkdownPage page, List<Block> run, MarkdownBlock? owner, List<MarkdownTextBlock> result)
    {
        var source = page.Source;
        List<string>? openElements = null;
        for (var index = 0; index < run.Count; index++)
        {
            var block = run[index];
            openElements?.Clear();
            var isUnterminated = ReadElements(source, block, ref openElements);
            if (block is not HtmlBlock && !isUnterminated && openElements is not { Count: > 0 })
            {
                result.Add(new MarkdownTextBlock(block, GetLine(block), GetEndLine(page, block), owner, IsEditable(block), HoldsHtml: false));
                continue;
            }

            while ((isUnterminated || openElements is { Count: > 0 }) && index + 1 < run.Count)
            {
                isUnterminated |= ReadElements(source, run[++index], ref openElements);
            }

            var leavesHtmlOpen = isUnterminated || openElements is { Count: > 0 };

            // The source of raw HTML is the markup that it renders.
            var start = Math.Clamp(block.Span.Start, 0, source.Length);
            var end = Math.Clamp(run[index].Span.End + 1, start, source.Length);
            var showsNothing = block is HtmlBlock && !ShowsSomething(source.AsSpan(start, end - start));
            result.Add(new MarkdownTextBlock(
                block, GetLine(block), GetEndLine(page, run[index]), owner, IsEditable: false, HoldsHtml: true, leavesHtmlOpen, showsNothing));
        }
    }

    /// <summary>
    /// Gets whether the markup shows anything besides white space, comments, other markup that starts with &lt;! or &lt;?,
    /// which a browser reads as a comment up to the next '&gt;', and script and style elements, any of which hides the rest of
    /// the markup when it is not closed.
    /// </summary>
    internal static bool ShowsSomething(ReadOnlySpan<char> html)
    {
        var rest = html;
        while (true)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty)
            {
                return false;
            }

            var endMarker = rest.StartsWith("<!--", StringComparison.Ordinal) ? "-->"
                : rest.Length > 1 && rest[0] == '<' && rest[1] is '!' or '?' ? ">"
                : IsStartTag(rest, "script") ? "</script>"
                : IsStartTag(rest, "style") ? "</style>"
                : null;
            if (endMarker is null)
            {
                return true;
            }

            var end = rest.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return false;
            }

            rest = rest[(end + endMarker.Length)..];
        }

        static bool IsStartTag(ReadOnlySpan<char> markup, string name) =>
            markup.Length > name.Length + 1 &&
            markup[0] == '<' &&
            markup[1..].StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
            !char.IsAsciiLetterOrDigit(markup[name.Length + 1]);
    }

    // Reads the HTML of the block into the names of the open elements: the source of raw HTML, and the tags of inline HTML,
    // also in a list or a quote, but not code. A block of the page closes what opens in it. Returns whether a construct is
    // not closed, which takes the rest of the run.
    private static bool ReadElements(string source, Block block, ref List<string>? openElements)
    {
        switch (block)
        {
            case MarkdownBlockContainer:
                return false;

            case HtmlBlock:
            {
                var start = Math.Clamp(block.Span.Start, 0, source.Length);
                var end = Math.Clamp(block.Span.End + 1, start, source.Length);
                return ReadTags(source.AsSpan(start, end - start), ref openElements);
            }

            case LeafBlock { Inline: { } inline }:
            {
                var isUnterminated = false;
                foreach (var html in inline.Descendants<HtmlInline>())
                {
                    isUnterminated |= ReadTags(html.Tag.AsSpan(), ref openElements);
                }

                return isUnterminated;
            }

            case ContainerBlock container:
            {
                var isUnterminated = false;
                foreach (var child in container)
                {
                    isUnterminated |= ReadElements(source, child, ref openElements);
                }

                return isUnterminated;
            }

            default:
                return false;
        }
    }

    // Reads the tags into the names of the open elements, much as a browser does: a start tag opens an element, also one
    // that ends with "/>", which a browser ignores except on void elements, and an end tag closes the last open element of
    // its name together with the ones opened after it. Comments, the other markup that starts with <! or <?, void elements
    // and the text of script, style, textarea and title elements open none. Returns whether a comment, a declaration, a tag
    // or such an element is not closed, which a browser lets take the rest of the markup.
    private static bool ReadTags(ReadOnlySpan<char> html, ref List<string>? openElements)
    {
        var index = 0;
        while (index < html.Length)
        {
            var tagStart = html[index..].IndexOf('<');
            if (tagStart < 0)
            {
                return false;
            }

            index += tagStart;
            var rest = html[index..];
            if (rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = rest[4..].IndexOf("-->", StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return true;
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
                    return true;
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
                return true;
            }

            index += tagEnd + 1;
            if (IsVoidElement(name))
            {
                continue;
            }

            if (isEndTag)
            {
                for (var open = (openElements?.Count ?? 0) - 1; open >= 0; open--)
                {
                    if (name.Equals(openElements![open], StringComparison.OrdinalIgnoreCase))
                    {
                        openElements.RemoveRange(open, openElements.Count - open);
                        break;
                    }
                }

                continue;
            }

            (openElements ??= []).Add(name.ToString());
            if (IsRawTextElement(name))
            {
                // Up to its end tag, which the loop reads next.
                var endTag = FindEndTag(html[index..], name);
                if (endTag < 0)
                {
                    return true;
                }

                index += endTag;
            }
        }

        return false;
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

    private static bool IsEditable(Block block) =>
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

using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Finds the regions of a page that the editor decorates: widget blocks and expressions.
/// </summary>
public static class MarkdownDecorationParser
{
    /// <summary>
    /// Gets one region per widget block, per slot block that created a subject, and per expression.
    /// The region of a block covers its head line and its json fence, not its content.
    /// Braces inside markdown code are no expression and get no region.
    /// </summary>
    /// <param name="content">The page text.</param>
    /// <param name="getBlockSubject">Returns the subject a block created, by block index.</param>
    public static List<DecorationRegion> Parse(
        string? content,
        Func<int, IInterceptorSubject?>? getBlockSubject = null)
    {
        return ParseDecorations(content, getBlockSubject).Regions;
    }

    /// <summary>
    /// Gets the regions of <see cref="Parse"/> together with the outline of every widget, slot and style
    /// block. Invalid blocks get no outline.
    /// </summary>
    /// <param name="content">The page text.</param>
    /// <param name="getBlockSubject">Returns the subject a block created, by block index.</param>
    public static MarkdownDecorations ParseDecorations(
        string? content,
        Func<int, IInterceptorSubject?>? getBlockSubject = null)
    {
        var decorations = new MarkdownDecorations([], []);
        if (string.IsNullOrEmpty(content))
        {
            return decorations;
        }

        var page = MarkdownPageParser.Parse(content);
        var lineStarts = GetLineStarts(content);

        AddOutlines(page.Blocks, 0, content, lineStarts, decorations.Outlines);

        foreach (var block in page.AllBlocks)
        {
            if (block.Head is null || block.Kind is not (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot))
            {
                continue;
            }

            var subject = getBlockSubject?.Invoke(block.Index);
            if (block.Kind == MarkdownBlockKind.Slot && subject is null)
            {
                continue;
            }

            var end = block.ConfigurationSpan?.End ?? block.HeadSpan.End;
            var (startLine, startColumn) = GetLineAndColumn(lineStarts, block.HeadSpan.Start);
            var (endLine, endColumn) = GetLineAndColumn(lineStarts, end);

            decorations.Regions.Add(new DecorationRegion(
                startLine,
                startColumn,
                endLine,
                endColumn,
                DecorationRegionType.SubjectBlock,
                GetName(block.Head),
                subject,
                block.Index));
        }

        foreach (var expression in MarkdownSegmentBuilder.FindExpressions(page))
        {
            var (startLine, startColumn) = GetLineAndColumn(lineStarts, expression.Span.Start);
            var (endLine, endColumn) = GetLineAndColumn(lineStarts, expression.Span.End);

            decorations.Regions.Add(new DecorationRegion(
                startLine, startColumn, endLine, endColumn, DecorationRegionType.Expression, expression.Path));
        }

        return decorations;
    }

    private static void AddOutlines(
        IReadOnlyList<MarkdownBlock> blocks, int depth, string content, int[] lineStarts, List<BlockOutline> outlines)
    {
        foreach (var block in blocks)
        {
            if (block.Kind == MarkdownBlockKind.Invalid)
            {
                continue;
            }

            var (startLine, column) = GetLineAndColumn(lineStarts, block.HeadSpan.Start);
            var (headEndLine, headEndColumn) = GetLineAndColumn(lineStarts, block.ConfigurationSpan?.End ?? block.HeadSpan.End);
            int? configurationStartLine = block.ConfigurationSpan is { } configurationSpan
                ? GetLineAndColumn(lineStarts, configurationSpan.Start).Line
                : null;

            var name = block.Head is { } head
                ? GetName(head)
                : content.AsSpan(block.HeadSpan.Start + block.FenceLength, block.HeadSpan.Length - block.FenceLength).Trim().ToString();

            outlines.Add(new BlockOutline(
                startLine,
                Math.Max(block.EndLine, startLine),
                configurationStartLine,
                headEndLine,
                headEndColumn,
                depth,
                name,
                new FencePosition(startLine, column, block.FenceLength),
                block.HasClosingLine ? FindClosingFence(content, lineStarts, block.EndLine) : null));

            AddOutlines(block.Children, depth + 1, content, lineStarts, outlines);
        }
    }

    private static string GetName(MarkdownBlockHead head) =>
        head.Id ?? head.Name[(head.Name.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Gets the fence characters of a closing line. They end the line, and what comes before them can be
    /// indentation or the markers of a quote or a list.
    /// </summary>
    private static FencePosition? FindClosingFence(string content, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length)
        {
            return null;
        }

        var lineStart = lineStarts[line - 1];
        var end = line < lineStarts.Length ? lineStarts[line] : content.Length;
        while (end > lineStart && char.IsWhiteSpace(content[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > lineStart && content[start - 1] == ':')
        {
            start--;
        }

        return start == end ? null : new FencePosition(line, start - lineStart + 1, end - start);
    }

    /// <summary>Gets the character index at which every line starts.</summary>
    private static int[] GetLineStarts(string content)
    {
        var lineStarts = new int[content.AsSpan().Count('\n') + 1];
        var line = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineStarts[line++] = i + 1;
            }
        }

        return lineStarts;
    }

    /// <summary>
    /// Converts a character index to line and column numbers (1-based).
    /// </summary>
    private static (int Line, int Column) GetLineAndColumn(int[] lineStarts, int index)
    {
        var line = Array.BinarySearch(lineStarts, index);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return (line + 1, index - lineStarts[line] + 1);
    }
}

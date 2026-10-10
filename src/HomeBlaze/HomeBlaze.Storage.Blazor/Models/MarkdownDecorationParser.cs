using System.Text.RegularExpressions;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Finds the regions of a page that the editor decorates: widget blocks and expressions.
/// </summary>
public static partial class MarkdownDecorationParser
{
    [GeneratedRegex(MarkdownSegmentBuilder.ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Gets one region per widget block, per slot block that created a subject, and per expression.
    /// The region of a block covers its head line and its json fence, not its content.
    /// </summary>
    /// <param name="content">The page text.</param>
    /// <param name="getBlockSubject">Returns the subject a block created, by block index.</param>
    public static List<DecorationRegion> Parse(
        string? content,
        Func<int, IInterceptorSubject?>? getBlockSubject = null)
    {
        var regions = new List<DecorationRegion>();
        if (string.IsNullOrEmpty(content))
        {
            return regions;
        }

        foreach (var block in MarkdownPageParser.Parse(content).AllBlocks)
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
            var (startLine, startColumn) = GetLineAndColumn(content, block.HeadSpan.Start);
            var (endLine, endColumn) = GetLineAndColumn(content, end);

            regions.Add(new DecorationRegion(
                startLine,
                startColumn,
                endLine,
                endColumn,
                DecorationRegionType.SubjectBlock,
                block.Head.Id ?? block.Head.Name[(block.Head.Name.LastIndexOf('.') + 1)..],
                subject,
                block.Index));
        }

        foreach (Match match in ExpressionRegex().Matches(content))
        {
            var (startLine, startColumn) = GetLineAndColumn(content, match.Index);
            var (endLine, endColumn) = GetLineAndColumn(content, match.Index + match.Length);

            regions.Add(new DecorationRegion(
                startLine, startColumn, endLine, endColumn, DecorationRegionType.Expression, match.Groups[1].Value));
        }

        return regions;
    }

    /// <summary>
    /// Converts a character index to line and column numbers (1-based).
    /// </summary>
    private static (int Line, int Column) GetLineAndColumn(string content, int index)
    {
        var line = 1;
        var column = 1;

        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }
}

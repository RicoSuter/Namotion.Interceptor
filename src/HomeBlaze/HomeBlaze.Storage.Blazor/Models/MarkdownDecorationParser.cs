using System.Text.RegularExpressions;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Parses markdown content to find decoration regions for subject blocks and expressions.
/// </summary>
public static partial class MarkdownDecorationParser
{
    /// <summary>
    /// Matches subject blocks: ```subject(name) ... ```
    /// </summary>
    [GeneratedRegex(@"```subject\(([^)]+)\)\s*\n([\s\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex SubjectBlockRegex();

    /// <summary>
    /// Matches expressions: {{ path }}
    /// </summary>
    [GeneratedRegex(@"\{\{\s*([^}]+)\s*\}\}")]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Parses markdown content and returns decoration regions.
    /// </summary>
    /// <param name="content">The markdown content to parse.</param>
    /// <param name="children">Dictionary of child subjects (for resolving subject blocks).</param>
    /// <returns>List of decoration regions found in the content.</returns>
    public static List<DecorationRegion> Parse(
        string? content,
        IDictionary<string, IInterceptorSubject>? children = null)
    {
        var regions = new List<DecorationRegion>();

        if (string.IsNullOrEmpty(content))
        {
            return regions;
        }

        // Parse subject blocks
        foreach (Match match in SubjectBlockRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            var (startLine, startColumn) = GetLineAndColumn(content, match.Index);
            var (endLine, endColumn) = GetLineAndColumn(content, match.Index + match.Length);

            IInterceptorSubject? subject = null;
            children?.TryGetValue(name, out subject);

            regions.Add(new DecorationRegion(
                StartLine: startLine,
                StartColumn: startColumn,
                EndLine: endLine,
                EndColumn: endColumn,
                Type: DecorationRegionType.SubjectBlock,
                Name: name,
                Subject: subject));
        }

        // Parse expressions
        foreach (Match match in ExpressionRegex().Matches(content))
        {
            var path = match.Groups[1].Value.Trim();
            var (startLine, startColumn) = GetLineAndColumn(content, match.Index);
            var (endLine, endColumn) = GetLineAndColumn(content, match.Index + match.Length);

            regions.Add(new DecorationRegion(
                StartLine: startLine,
                StartColumn: startColumn,
                EndLine: endLine,
                EndColumn: endColumn,
                Type: DecorationRegionType.Expression,
                Name: path,
                Subject: null));
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

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides search_content, which finds text in the Markdown pages and documents of the graph. It searches the source of
/// each page as <see cref="MarkdownFile.ReadRawContent"/> shows it, with secret values hidden, so a hidden value can
/// neither match nor appear in a snippet. The tool only reads and is also offered when the configuration is read-only.
/// </summary>
public class HomeBlazeContentSearchToolProvider : IMcpToolProvider
{
    /// <summary>The name of the tool that finds text in Markdown pages and documents.</summary>
    public const string SearchContentToolName = "search_content";

    internal const int DefaultMaxPages = 20;
    internal const int MaxPagesLimit = 100;
    internal const int MaxSearchedPages = 2000;
    internal const int MaxMatchesPerPage = 5;
    internal const int SnippetLength = 160;
    private const int SnippetLeadLength = 60;

    internal static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    private static readonly string Description =
        "Find text in Markdown pages and documents (subjects whose name ends in .md), such as the documentation and notes of " +
        $"this installation. Searches the Markdown source as {HomeBlazeMcpToolProvider.InvokeQueryToolName} method ReadRawContent " +
        "returns it, with {{ path }} expressions unevaluated and secret values hidden (***). Returns the matching pages in path " +
        $"order, each with its title, the number of matching lines and the line number and a snippet of up to {MaxMatchesPerPage} " +
        $"of them. truncated is true when more pages match than max_pages or the search stopped at its limit of {MaxSearchedPages} " +
        $"pages or {SearchTimeout.TotalSeconds:0} seconds; narrow it with path. Read a found page with " +
        $"{HomeBlazeMcpToolProvider.InvokeQueryToolName} method ReadEvaluatedContent or ReadRawContent.";

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "The text to find within single lines, or a .NET regular expression when regex is true" },
            regex = new { type = "boolean", description = "Whether query is a regular expression (default: false)" },
            ignore_case = new { type = "boolean", description = "Whether case is ignored (default: true)" },
            path = new { type = "string", description = "Only search pages in or under this path, for example /Docs (default: all pages)" },
            max_pages = new { type = "integer", description = $"The most pages to return (default: {DefaultMaxPages}, at most {MaxPagesLimit})" }
        },
        required = new[] { "query" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;

    /// <param name="rootSubjectProvider">Resolves the root subject when the tool runs.</param>
    /// <param name="pathProvider">Builds the paths of the pages, as the other tools resolve them.</param>
    public HomeBlazeContentSearchToolProvider(Func<IInterceptorSubject> rootSubjectProvider, PathProviderBase pathProvider)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = SearchContentToolName,
            Description = Description,
            InputSchema = Schema,
            Handler = HandleAsync,
            Annotations = McpToolAnnotations.ReadOnly
        };
    }

    private Task<object?> HandleAsync(JsonElement input, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(Search(input, cancellationToken));

    private object Search(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("query", out var queryElement) ||
            queryElement.ValueKind != JsonValueKind.String ||
            queryElement.GetString() is not { Length: > 0 } queryText)
        {
            return new { error = "Missing parameter 'query'." };
        }

        if (!TryCreateQuery(input, queryText, out var query, out var queryError))
        {
            return new { error = queryError };
        }

        var rootSubject = _rootSubjectProvider();
        var registry = rootSubject.Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return new { error = "Subject registry is not available." };
        }

        var pathPrefix = input.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? NormalizePathPrefix(pathElement.GetString())
            : null;

        var files = FindFiles(registry, rootSubject, pathPrefix);
        return SearchFiles(files, query, GetMaxPages(input), cancellationToken);
    }

    private static bool TryCreateQuery(JsonElement input, string queryText, out ContentQuery query, out string? error)
    {
        var ignoreCase = GetBoolean(input, "ignore_case", defaultValue: true);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        error = null;
        query = new ContentQuery(queryText, null, comparison);
        if (!GetBoolean(input, "regex", defaultValue: false))
        {
            return true;
        }

        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            query = new ContentQuery(queryText, new Regex(queryText, options, RegexMatchTimeout), comparison);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = $"Invalid regular expression: {exception.Message}";
            return false;
        }
    }

    private static int GetMaxPages(JsonElement input) =>
        input.TryGetProperty("max_pages", out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, MaxPagesLimit)
            : DefaultMaxPages;

    private static object SearchFiles(
        List<(string Path, MarkdownFile File)> files, ContentQuery query, int maxPages, CancellationToken cancellationToken)
    {
        var truncated = files.Count > MaxSearchedPages;
        var deadline = Stopwatch.GetTimestamp() + (long)(SearchTimeout.TotalSeconds * Stopwatch.Frequency);
        var pages = new List<object>();
        var pagesSearched = 0;

        var fileCount = Math.Min(files.Count, MaxSearchedPages);
        for (var index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, file) = files[index];

            // The view with secret values hidden; null while the page was not parsed yet.
            var content = file.ReadRawContent();
            pagesSearched++;
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            // Once max_pages pages matched, one more hit only decides truncated, so the scan stops at it.
            var isFull = pages.Count == maxPages;
            PageScan scan;
            try
            {
                scan = ScanPage(content, query, deadline, stopAtFirstMatch: isFull);
            }
            catch (RegexMatchTimeoutException)
            {
                return new { error = $"The regular expression took longer than {RegexMatchTimeout.TotalMilliseconds:0} ms on a line of '{path}'; simplify it." };
            }

            if (scan.MatchCount > 0)
            {
                if (isFull)
                {
                    truncated = true;
                    break;
                }

                pages.Add(new { path, title = file.Title, match_count = scan.MatchCount, matches = scan.Matches });
            }

            if (scan.IsTimedOut)
            {
                truncated = true;
                break;
            }
        }

        return new { pages, truncated, pages_searched = pagesSearched };
    }

    private static PageScan ScanPage(string content, ContentQuery query, long deadline, bool stopAtFirstMatch)
    {
        List<object>? matches = null;
        var matchCount = 0;
        var lineNumber = 0;
        foreach (var line in content.AsSpan().EnumerateLines())
        {
            lineNumber++;
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return new PageScan(matchCount, matches, IsTimedOut: true);
            }

            var matchIndex = query.FindMatch(line);
            if (matchIndex < 0)
            {
                continue;
            }

            matchCount++;
            if (stopAtFirstMatch)
            {
                break;
            }

            if (matchCount <= MaxMatchesPerPage)
            {
                (matches ??= []).Add(new { line = lineNumber, snippet = CreateSnippet(line, matchIndex) });
            }
        }

        return new PageScan(matchCount, matches, IsTimedOut: false);
    }

    private static string CreateSnippet(ReadOnlySpan<char> line, int matchIndex)
    {
        if (line.Length <= SnippetLength)
        {
            return line.Trim().ToString();
        }

        var start = Math.Clamp(matchIndex - SnippetLeadLength, 0, line.Length - SnippetLength);
        var end = start + SnippetLength;
        return string.Concat(start > 0 ? "…" : "", line[start..end].Trim(), end < line.Length ? "…" : "");
    }

    // The Markdown files in or under the prefix, in path order so that results and truncation are stable.
    private List<(string Path, MarkdownFile File)> FindFiles(ISubjectRegistry registry, IInterceptorSubject rootSubject, string? pathPrefix)
    {
        var files = new List<(string Path, MarkdownFile File)>();
        foreach (var (_, registered) in registry.KnownSubjects)
        {
            if (registered.Subject is MarkdownFile file &&
                TryGetPath(registered, rootSubject) is { } path &&
                (pathPrefix is null || IsInOrUnder(path, pathPrefix)))
            {
                files.Add((path, file));
            }
        }

        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
    }

    // The path as browse and search show it: the path through the first parent, after the leading slash.
    private string? TryGetPath(RegisteredSubject subject, IInterceptorSubject rootSubject)
    {
        if (subject.Parents.Length == 0)
        {
            return null;
        }

        var parent = subject.Parents[0];
        var path = parent.Property.TryGetPath(_pathProvider, rootSubject, parent.Index);
        return path is not null ? "/" + path : null;
    }

    private static string? NormalizePathPrefix(string? pathPrefix)
    {
        var trimmed = pathPrefix?.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed[0] == '/' ? trimmed : "/" + trimmed;
    }

    // Whole segments only, so /Docs does not match /DocsArchive/Page.md.
    private static bool IsInOrUnder(string path, string pathPrefix) =>
        path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == pathPrefix.Length || path[pathPrefix.Length] == '/');

    private static bool GetBoolean(JsonElement input, string name, bool defaultValue) =>
        input.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : defaultValue;

    private readonly record struct PageScan(int MatchCount, List<object>? Matches, bool IsTimedOut);

    private readonly record struct ContentQuery(string Text, Regex? Regex, StringComparison Comparison)
    {
        // The index of the first match in the line, or -1.
        public int FindMatch(ReadOnlySpan<char> line)
        {
            if (Regex is null)
            {
                return line.IndexOf(Text, Comparison);
            }

            var matches = Regex.EnumerateMatches(line);
            return matches.MoveNext() ? matches.Current.Index : -1;
        }
    }
}

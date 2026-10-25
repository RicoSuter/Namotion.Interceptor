using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides search_markdown, which finds text in the Markdown pages and documents of the graph and in the notes of the
/// metadata of subjects and properties. It searches the source of each page as <see cref="MarkdownFile.ReadRawContent"/>
/// shows it, with secret values hidden, so a hidden value can neither match nor appear in a snippet; notes hold no
/// secrets. The tool only reads and is also offered when the configuration is read-only.
/// </summary>
public class HomeBlazeMarkdownSearchToolProvider : IMcpToolProvider
{
    /// <summary>The name of the tool that finds text in Markdown pages and documents.</summary>
    public const string SearchMarkdownToolName = "search_markdown";

    private const int DefaultMaxPages = 20;
    private const int MaxPagesLimit = 100;
    private const int MaxSearchedPages = 2000;
    private const int MaxMatchesPerPage = 5;
    private const int SnippetLength = 160;
    private const int SnippetLeadLength = 60;

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);

    private static readonly string Description =
        "Find text in Markdown pages and documents (subjects whose name ends in .md), such as the documentation and notes of " +
        "this installation, and in the notes of metadata of subjects and properties (the key notes of HomeBlaze:Metadata). " +
        $"Searches the Markdown source as {HomeBlazeMcpToolProvider.InvokeQueryToolName} method ReadRawContent returns it, with " +
        "{{ path }} expressions unevaluated and secret values hidden (***). Returns the matching pages and notes in path order, " +
        $"each with its title, the number of matching lines and the line number and a snippet of up to {MaxMatchesPerPage} of " +
        "them; notes have kind notes, the path of their subject, for the notes of a property property, its name, and read, " +
        "the path that get_property reads them with. " +
        $"truncated is true when more match than max_pages or the search stopped at its limit of {MaxSearchedPages} " +
        $"pages and notes or {SearchTimeout.TotalSeconds:0} seconds; narrow it with path. Read a found page with " +
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
            max_pages = new { type = "integer", description = $"The most pages and notes to return (default: {DefaultMaxPages}, at most {MaxPagesLimit})" }
        },
        required = new[] { "query" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly string _pathPrefix;
    private readonly MarkdownSearchSourceFinder _sourceFinder;

    /// <param name="rootSubjectProvider">Resolves the root subject when the tool runs.</param>
    /// <param name="pathProvider">Builds the paths of the pages, as the other tools resolve them.</param>
    /// <param name="pathPrefix">The prefix of every path, <see cref="McpServerConfiguration.PathPrefix"/> of the configuration.</param>
    /// <param name="excludeTypes">
    /// <see cref="McpServerConfiguration.ExcludeTypes"/> of the configuration: the pages and notes of a subject of one of
    /// these types, or below one through the first parents, are not searched.
    /// </param>
    /// <param name="forAgents">Whether the tool serves the built-in agents, which do not search the notes of agents.</param>
    internal HomeBlazeMarkdownSearchToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider, PathProviderBase pathProvider, string pathPrefix, Type[] excludeTypes, bool forAgents)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathPrefix = pathPrefix;
        _sourceFinder = new MarkdownSearchSourceFinder(pathProvider, pathPrefix, excludeTypes, forAgents);
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = SearchMarkdownToolName,
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

        if (!TryGetPathPrefix(input, out var pathPrefix))
        {
            return new { error = "Parameter 'path' must be a string." };
        }

        var rootSubject = _rootSubjectProvider();
        var registry = rootSubject.TryGetContext()?.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return new { error = "Subject registry is not available." };
        }

        var sources = _sourceFinder.Find(registry, rootSubject, pathPrefix);
        return SearchSources(sources, query, GetMaxPages(input), cancellationToken);
    }

    private static bool TryCreateQuery(JsonElement input, string queryText, out ContentQuery query, out string? error)
    {
        var ignoreCase = GetBoolean(input, "ignore_case", defaultValue: true);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Regex? regex = null;
        if (GetBoolean(input, "regex", defaultValue: false))
        {
            try
            {
                var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                regex = new Regex(queryText, options, RegexMatchTimeout);
            }
            catch (ArgumentException exception)
            {
                query = default;
                error = $"Invalid regular expression: {exception.Message}";
                return false;
            }
        }

        query = new ContentQuery(queryText, regex, comparison);
        error = null;
        return true;
    }

    private static int GetMaxPages(JsonElement input) =>
        input.TryGetProperty("max_pages", out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, MaxPagesLimit)
            : DefaultMaxPages;

    private static object SearchSources(
        List<MarkdownSearchSource> sources, ContentQuery query, int maxPages, CancellationToken cancellationToken)
    {
        var truncated = sources.Count > MaxSearchedPages;
        var deadline = Stopwatch.GetTimestamp() + (long)(SearchTimeout.TotalSeconds * Stopwatch.Frequency);
        var pages = new List<object>();
        var pagesSearched = 0;

        var sourceCount = Math.Min(sources.Count, MaxSearchedPages);
        for (var index = 0; index < sourceCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index];
            var path = source.Path;
            var content = source.ReadContent();
            if (content is null)
            {
                continue;
            }

            pagesSearched++;

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

                pages.Add(source switch
                {
                    { File: not null } => new { path, title = source.Title, match_count = scan.MatchCount, matches = scan.Matches },
                    { Property: { } property } => new { path, property, title = source.Title, kind = "notes", read = GetMetadataPath(path, property), match_count = scan.MatchCount, matches = scan.Matches },
                    _ => (object)new { path, title = source.Title, kind = "notes", read = GetMetadataPath(path, null), match_count = scan.MatchCount, matches = scan.Matches }
                });
            }

            if (scan.IsTimedOut)
            {
                truncated = true;
                break;
            }
        }

        return new { pages, truncated, pages_searched = pagesSearched };
    }

    // The path of the metadata of a subject, or of its property, with one slash after the root path /.
    private static string GetMetadataPath(string path, string? property)
    {
        var separator = path.EndsWith('/') ? "" : "/";
        return property is null
            ? string.Concat(path, separator, KnownAttributes.Metadata)
            : string.Concat(path, separator, property, "@" + KnownAttributes.Metadata);
    }

    private static PageScan ScanPage(string content, ContentQuery query, long deadline, bool stopAtFirstMatch)
    {
        List<object>? matches = null;
        var matchCount = 0;
        var lineNumber = 0;
        var remaining = content.AsSpan();
        while (remaining.Length > 0)
        {
            var line = TakeLine(ref remaining);
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

    // Only \n separates lines, as in editors and Markdown parsers; EnumerateLines would also split on U+2028 and others.
    private static ReadOnlySpan<char> TakeLine(ref ReadOnlySpan<char> remaining)
    {
        var lineEnd = remaining.IndexOf('\n');
        var line = lineEnd < 0 ? remaining : remaining[..lineEnd];
        remaining = lineEnd < 0 ? default : remaining[(lineEnd + 1)..];
        return line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;
    }

    private static string CreateSnippet(ReadOnlySpan<char> line, int matchIndex)
    {
        if (line.Length <= SnippetLength)
        {
            return line.Trim().ToString();
        }

        var start = Math.Clamp(matchIndex - SnippetLeadLength, 0, line.Length - SnippetLength);
        var end = start + SnippetLength;

        // Do not emit half of a surrogate pair at either edge of the window.
        if (start > 0 && char.IsLowSurrogate(line[start]))
        {
            start++;
        }

        if (end < line.Length && char.IsHighSurrogate(line[end - 1]))
        {
            end--;
        }

        return string.Concat(start > 0 ? "…" : "", line[start..end].Trim(), end < line.Length ? "…" : "");
    }

    // A missing or null path searches all pages; any other value that is not a string is refused, as by the other tools.
    private bool TryGetPathPrefix(JsonElement input, out string? pathPrefix)
    {
        pathPrefix = null;
        if (!input.TryGetProperty("path", out var pathElement) || pathElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (pathElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        pathPrefix = NormalizePathPrefix(pathElement.GetString());
        return true;
    }

    // Spelled as the page paths are, after the path prefix, so a path with or without a leading or trailing slash matches.
    private string? NormalizePathPrefix(string? pathPrefix)
    {
        var trimmed = pathPrefix?.Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : _pathPrefix + trimmed;
    }

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

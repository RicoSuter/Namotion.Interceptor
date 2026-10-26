using System.Text.Json;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Watch path matching of agents and the state at their watch paths.
/// </summary>
internal static class AgentWatchPaths
{
    /// <summary>
    /// Trims entries, drops blank ones, adds a leading slash and removes a trailing one.
    /// </summary>
    public static string[] Normalize(IEnumerable<string>? watchPaths)
    {
        return (watchPaths ?? [])
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Select(path => "/" + path.Trim('/'))
            .ToArray();
    }

    /// <summary>
    /// Whether the property <paramref name="propertyName"/> of the subject at the canonical
    /// <paramref name="subjectPath"/> lies in one of the normalized watch paths, without joining the two.
    /// </summary>
    public static bool Matches(string subjectPath, string propertyName, IReadOnlyList<string> normalizedWatchPaths)
    {
        // The property path is subjectPart + "/" + propertyName; the root's subject part is empty.
        var subjectPart = subjectPath == "/" ? ReadOnlySpan<char>.Empty : subjectPath.AsSpan();
        foreach (var watchPath in normalizedWatchPaths)
        {
            if (watchPath == "/" || MatchesWatchPath(subjectPart, propertyName, watchPath.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fetches the state at each of the normalized watch paths with the read-only tools of <paramref name="toolSource"/>
    /// under <paramref name="accessPolicy"/>; a path whose state cannot be fetched gets an error state.
    /// </summary>
    /// <param name="normalizedWatchPaths">The watch paths, normalized with <see cref="Normalize"/>.</param>
    /// <param name="toolSource">Creates the tools that read the state.</param>
    /// <param name="accessPolicy">
    /// The policy of the tools: the operator chose the paths, so not the agent's roles, but one that keeps the system
    /// rules, such as that of the <c>Everything</c> role.
    /// </param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public static async Task<IReadOnlyList<AgentWatchedState>> FetchStateAsync(
        string[] normalizedWatchPaths, IAgentToolSource toolSource, IMcpAccessPolicy accessPolicy, CancellationToken cancellationToken)
    {
        if (normalizedWatchPaths.Length == 0)
        {
            return [];
        }

        var tools = toolSource.CreateTools(isReadOnly: true, accessPolicy);
        var browse = tools.FirstOrDefault(tool => tool.Name == "browse");
        var getProperty = tools.FirstOrDefault(tool => tool.Name == "get_property");
        var states = new List<AgentWatchedState>(normalizedWatchPaths.Length);
        foreach (var path in normalizedWatchPaths)
        {
            try
            {
                states.Add(await FetchPathStateAsync(path, browse, getProperty, cancellationToken));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                states.Add(new AgentWatchedState(path, AgentWatchedStateKind.Error, ToJsonString("Error: " + exception.Message)));
            }
        }

        return states;
    }

    private static bool MatchesWatchPath(ReadOnlySpan<char> subjectPart, string propertyName, ReadOnlySpan<char> watch)
    {
        if (watch.Length <= subjectPart.Length)
        {
            // The subject part continues with "/" when it is used up exactly.
            return StartsWithSegment(subjectPart, watch);
        }

        return watch.StartsWith(subjectPart, StringComparison.Ordinal) &&
            watch[subjectPart.Length] == '/' &&
            StartsWithSegment(propertyName, watch[(subjectPart.Length + 1)..]);
    }

    private static bool StartsWithSegment(ReadOnlySpan<char> text, ReadOnlySpan<char> prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) &&
        (prefix.Length == text.Length || IsSegmentBoundary(text[prefix.Length]));

    private static bool IsSegmentBoundary(char character) => character is '/' or '[';

    /// <summary>
    /// Browses the subject at a watch path, or reads the property there when no subject is, since browse only
    /// resolves subjects.
    /// </summary>
    private static async Task<AgentWatchedState> FetchPathStateAsync(
        string path, McpToolInfo? browse, McpToolInfo? getProperty, CancellationToken cancellationToken)
    {
        string? error = null;
        if (browse is not null)
        {
            var input = JsonSerializer.SerializeToElement(new AgentBrowseInput(path, Depth: 2, IncludeProperties: true, Format: "text"), AgentJsonContext.Default.AgentBrowseInput);
            var result = await browse.Handler(input, cancellationToken);
            if (result is string text)
            {
                return new AgentWatchedState(path, AgentWatchedStateKind.Subject, ToJsonString(text));
            }

            // Tool results are open-ended objects, so only they are serialized without generated metadata.
            error = TryGetError(JsonSerializer.SerializeToElement(result));
        }

        if (getProperty is not null)
        {
            var input = JsonSerializer.SerializeToElement(new AgentPathInput(path), AgentJsonContext.Default.AgentPathInput);
            var result = JsonSerializer.SerializeToElement(await getProperty.Handler(input, cancellationToken));
            if (TryGetError(result) is null && result.ValueKind == JsonValueKind.Object)
            {
                return new AgentWatchedState(path, AgentWatchedStateKind.Property, result);
            }
        }

        return new AgentWatchedState(path, AgentWatchedStateKind.Error, ToJsonString(error ?? $"No subject or property found at {path}."));
    }

    private static JsonElement ToJsonString(string text) => JsonSerializer.SerializeToElement(text, AgentJsonContext.Default.String);

    private static string? TryGetError(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) ? error.ToString() : null;
}

/// <summary>The input of the browse tool for the state of a watch path.</summary>
internal sealed record AgentBrowseInput(string Path, int Depth, bool IncludeProperties, string Format);

/// <summary>The input of the get_property tool for the state of a watch path.</summary>
internal sealed record AgentPathInput(string Path);

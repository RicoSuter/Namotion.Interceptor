using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

public class SearchResult
{
    [JsonPropertyName("results")]
    public required Dictionary<string, SubjectNode> Results { get; init; }

    [JsonPropertyName("subjectCount")]
    public int SubjectCount { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>
    /// Problems with the request that did not prevent an answer, such as a property name no subject in scope has.
    /// </summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; }
}

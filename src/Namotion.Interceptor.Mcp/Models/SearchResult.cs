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
}

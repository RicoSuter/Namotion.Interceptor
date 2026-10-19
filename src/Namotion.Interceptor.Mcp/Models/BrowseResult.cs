using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

public class BrowseResult
{
    [JsonPropertyName("result")]
    public required SubjectNode Result { get; init; }

    [JsonPropertyName("subjectCount")]
    public int SubjectCount { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <inheritdoc cref="SearchResult.Warnings"/>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; }
}

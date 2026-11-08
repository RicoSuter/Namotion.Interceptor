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

    /// <summary>
    /// The children left out where maxSubjects or the server limit cut the tree. Their own children are not counted.
    /// </summary>
    [JsonPropertyName("omittedCount")]
    public int OmittedCount { get; init; }

    /// <inheritdoc cref="SearchResult.Warnings"/>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; }
}

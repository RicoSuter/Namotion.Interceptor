using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

/// <summary>
/// Represents a subject in the MCP tool output tree.
/// </summary>
public class SubjectNode
{
    [JsonPropertyName("$path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("$type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("$title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>
    /// Additional enrichments (e.g., $icon, $customField).
    /// Merged as top-level JSON properties via JsonExtensionData.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Enrichments { get; set; }

    [JsonPropertyName("methods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Methods { get; init; }

    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Interfaces { get; init; }

    /// <summary>
    /// Subject properties (scalar values, child subjects, collections).
    /// Uses set (not init) because BrowseTool adds subject-containing properties
    /// in a second pass after initial construction via McpToolHelper.BuildSubjectNodeDto.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubjectNodeProperty>? Properties { get; set; }
}

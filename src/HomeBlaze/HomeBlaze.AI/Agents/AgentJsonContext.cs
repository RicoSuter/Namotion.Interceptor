using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.AI.Agents.ClaudeCli;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Source-generated serialization metadata for the agent data files and the watched-state lookups, avoiding
/// reflection-based serialization and keeping <see cref="AgentTimelineEntryKind"/> as a readable string on disk.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentTimelineEntry))]
[JsonSerializable(typeof(ClaudeCliSessionFile))]
[JsonSerializable(typeof(AgentMemoryFile))]
[JsonSerializable(typeof(AgentStats))]
[JsonSerializable(typeof(AgentBrowseInput))]
[JsonSerializable(typeof(AgentPathInput))]
[JsonSerializable(typeof(string))]
internal partial class AgentJsonContext : JsonSerializerContext
{
}

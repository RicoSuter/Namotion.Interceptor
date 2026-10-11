using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.AI.Agents.Cli;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Source-generated serialization metadata for the agent timeline and session files, avoiding reflection-based
/// serialization and keeping <see cref="AgentTimelineEntryKind"/> as a readable string on disk.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentTimelineEntry))]
[JsonSerializable(typeof(ClaudeCliSessionFile))]
internal partial class AgentJsonContext : JsonSerializerContext
{
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Source-generated serialization metadata for the agent timeline file, avoiding reflection-based
/// serialization and keeping <see cref="AgentTimelineEntryKind"/> as a readable string on disk.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentTimelineEntry))]
internal partial class AgentJsonContext : JsonSerializerContext
{
}

using System.Text.Json.Serialization;

namespace HomeBlaze.Automation.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AutomationCheckpoint))]
internal sealed partial class AutomationCheckpointJsonContext : JsonSerializerContext;

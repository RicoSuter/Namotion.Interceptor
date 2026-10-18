using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PushoverResponse))]
internal sealed partial class PushoverJsonContext : JsonSerializerContext;

using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

[JsonSerializable(typeof(PushoverResponse))]
internal sealed partial class PushoverJsonContext : JsonSerializerContext;

using System.Text.Json.Serialization;

namespace Namotion.Services.Pushover.Client;

[JsonSerializable(typeof(PushoverResponse))]
internal sealed partial class PushoverJsonContext : JsonSerializerContext;

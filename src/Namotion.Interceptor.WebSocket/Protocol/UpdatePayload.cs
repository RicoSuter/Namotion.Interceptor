using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Update message payload. Inherits SubjectUpdate and adds an optional sequence number.
/// Server-to-client messages set Sequence; client-to-server messages leave it null.
/// </summary>
public class UpdatePayload : SubjectUpdate
{
    [JsonPropertyName("sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Sequence { get; set; }
}

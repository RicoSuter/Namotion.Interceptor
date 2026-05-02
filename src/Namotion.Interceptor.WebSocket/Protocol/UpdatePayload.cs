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

    /// <summary>
    /// Structural hash of the server's graph at the time this update was created.
    /// Clients compare against their own hash after applying to detect divergence.
    /// Null when sent by clients or when hashing is not supported.
    /// </summary>
    [JsonPropertyName("structuralHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StructuralHash { get; set; }
}

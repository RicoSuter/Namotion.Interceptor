using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Update message payload. Inherits SubjectUpdate and adds an optional sequence number.
/// Server-to-client messages set Sequence; client-to-server messages leave it null.
/// </summary>
public class UpdatePayload : SubjectUpdate
{
    /// <summary>
    /// Creates an empty payload, used by deserialization.
    /// </summary>
    [JsonConstructor]
    public UpdatePayload()
    {
    }

    /// <summary>
    /// Creates a payload carrying every field of <paramref name="update"/>, so that a field added to
    /// <see cref="SubjectUpdate"/> cannot go missing from the wire on the way through this type.
    /// </summary>
    /// <param name="update">The update to send.</param>
    public UpdatePayload(SubjectUpdate update)
        : base(update)
    {
    }

    [JsonPropertyName("sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Sequence { get; set; }
}

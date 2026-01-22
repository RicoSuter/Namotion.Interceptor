using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// JSON serializer for WebSocket messages. This class is stateless and thread-safe.
/// </summary>
public class JsonWebSocketSerializer : IWebSocketSerializer
{
    /// <summary>
    /// Shared singleton instance. Use this to avoid allocating a new serializer per connection.
    /// </summary>
    public static JsonWebSocketSerializer Instance { get; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public WebSocketFormat Format => WebSocketFormat.Json;

    public byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes, Options)
            ?? throw new InvalidOperationException("Deserialization returned null");
    }

    public byte[] SerializeMessage<T>(MessageType messageType, int? correlationId, T payload)
    {
        var envelope = new object?[] { (int)messageType, correlationId, payload };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    public void SerializeMessageTo<T>(IBufferWriter<byte> bufferWriter, MessageType messageType, int? correlationId, T payload)
    {
        using var writer = new Utf8JsonWriter(bufferWriter);
        writer.WriteStartArray();
        writer.WriteNumberValue((int)messageType);

        if (correlationId.HasValue)
        {
            writer.WriteNumberValue(correlationId.Value);
        }
        else
        {
            writer.WriteNullValue();
        }

        JsonSerializer.Serialize(writer, payload, Options);
        writer.WriteEndArray();
        writer.Flush();
    }

    public (MessageType Type, int? CorrelationId, int PayloadStart, int PayloadLength) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new InvalidOperationException("Invalid message envelope: expected array");
        }

        if (!reader.Read())
        {
            throw new InvalidOperationException("Invalid message envelope: missing messageType");
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new InvalidOperationException("Invalid message envelope: messageType must be a number");
        }
        var messageType = (MessageType)reader.GetInt32();

        if (!reader.Read())
        {
            throw new InvalidOperationException("Invalid message envelope: missing correlationId");
        }
        int? correlationId = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();

        if (!reader.Read())
        {
            throw new InvalidOperationException("Invalid message envelope: missing payload");
        }

        var payloadStart = (int)reader.TokenStartIndex;
        reader.Skip();
        var payloadLength = (int)reader.BytesConsumed - payloadStart;

        return (messageType, correlationId, payloadStart, payloadLength);
    }
}

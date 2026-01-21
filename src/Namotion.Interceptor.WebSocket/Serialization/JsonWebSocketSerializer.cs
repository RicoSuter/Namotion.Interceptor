using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// JSON serializer for WebSocket messages.
/// </summary>
public class JsonWebSocketSerializer : IWebSocketSerializer
{
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

    public (MessageType Type, int? CorrelationId, ReadOnlyMemory<byte> PayloadBytes) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes)
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

        var payloadEnd = (int)reader.BytesConsumed;
        var payloadBytes = bytes.Slice(payloadStart, payloadEnd - payloadStart).ToArray();
        return (messageType, correlationId, payloadBytes);
    }
}

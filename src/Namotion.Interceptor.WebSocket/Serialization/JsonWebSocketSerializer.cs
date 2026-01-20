using System;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var array = JsonSerializer.Deserialize<JsonArray>(bytes, Options)
            ?? throw new InvalidOperationException("Invalid message envelope");

        if (array.Count < 3)
        {
            throw new InvalidOperationException("Message envelope must have at least 3 elements");
        }

        var messageTypeNode = array[0];
        if (messageTypeNode is null)
        {
            throw new InvalidOperationException("Message envelope element [0] (messageType) is null");
        }

        var messageType = (MessageType)messageTypeNode.GetValue<int>();
        var correlationId = array[1]?.GetValue<int>();

        // Re-serialize payload element to bytes for later deserialization
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(array[2], Options);

        return (messageType, correlationId, payloadBytes);
    }
}

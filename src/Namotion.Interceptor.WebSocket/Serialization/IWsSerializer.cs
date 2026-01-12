using System;
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// Serializer interface for WebSocket messages.
/// Extensible for future format support (e.g., MessagePack).
/// </summary>
public interface IWsSerializer
{
    /// <summary>
    /// Gets the serialization format.
    /// </summary>
    WsFormat Format { get; }

    /// <summary>
    /// Serializes a payload to bytes.
    /// </summary>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes bytes to a payload.
    /// </summary>
    T Deserialize<T>(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Serializes a complete message envelope [MessageType, CorrelationId, Payload].
    /// </summary>
    byte[] SerializeMessage<T>(MessageType messageType, int? correlationId, T payload);

    /// <summary>
    /// Deserializes a message envelope and returns the message type, correlation ID, and raw payload bytes.
    /// </summary>
    (MessageType Type, int? CorrelationId, ReadOnlyMemory<byte> PayloadBytes) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes);
}

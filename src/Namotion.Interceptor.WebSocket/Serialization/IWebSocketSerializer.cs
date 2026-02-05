using System;
using System.Buffers;
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// Serializer interface for WebSocket messages.
/// Extensible for future format support (e.g., MessagePack).
/// </summary>
public interface IWebSocketSerializer
{
    /// <summary>
    /// Gets the serialization format.
    /// </summary>
    WebSocketFormat Format { get; }

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
    /// Serializes a complete message envelope [MessageType, CorrelationId, Payload] to a buffer writer.
    /// This avoids allocating a new byte array for each message.
    /// </summary>
    void SerializeMessageTo<T>(IBufferWriter<byte> bufferWriter, MessageType messageType, int? correlationId, T payload);

    /// <summary>
    /// Deserializes a message envelope and returns the message type, correlation ID, and payload byte range.
    /// The payload bytes reference the original input buffer and must be processed before the buffer is reused.
    /// </summary>
    (MessageType Type, int? CorrelationId, int PayloadStart, int PayloadLength) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes);
}

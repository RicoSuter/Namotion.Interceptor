using System;
using System.Buffers;

namespace Namotion.Interceptor.Mqtt;

/// <summary>
/// Converts values between CLR types and MQTT message payloads.
/// </summary>
public interface IMqttValueConverter
{
    /// <summary>
    /// Serializes a value to an MQTT message payload.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="type">The type of the value.</param>
    /// <returns>The serialized payload.</returns>
    byte[] Serialize(object? value, Type type);

    /// <summary>
    /// Deserializes an MQTT message payload to a value.
    /// </summary>
    /// <param name="payload">The payload to deserialize.</param>
    /// <param name="type">The target type.</param>
    /// <returns>The deserialized value.</returns>
    object? Deserialize(ReadOnlySequence<byte> payload, Type type);
}

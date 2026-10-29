using System;
using System.Buffers.Text;
using System.Collections.Generic;
using MQTTnet.Packets;

namespace Namotion.Interceptor.Mqtt;

internal static class MqttHelper
{
    /// <summary>
    /// Default timestamp serializer: converts to Unix milliseconds as UTF8 bytes.
    /// </summary>
    public static byte[] DefaultSerializeTimestamp(DateTimeOffset timestamp)
    {
        Span<byte> buffer = stackalloc byte[20];
        Utf8Formatter.TryFormat(timestamp.ToUnixTimeMilliseconds(), buffer, out var bytesWritten);
        return buffer[..bytesWritten].ToArray();
    }

    /// <summary>
    /// Default timestamp deserializer: parses Unix milliseconds from UTF8 bytes.
    /// </summary>
    public static DateTimeOffset? DefaultDeserializeTimestamp(ReadOnlyMemory<byte> value)
    {
        if (!Utf8Parser.TryParse(value.Span, out long unixMs, out var bytesConsumed) ||
            bytesConsumed != value.Length ||
            unixMs < -62135596800000 ||
            unixMs > 253402300799999)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    public static DateTimeOffset? ExtractSourceTimestamp(
        IReadOnlyCollection<MqttUserProperty>? userProperties,
        string? timestampPropertyName,
        Func<ReadOnlyMemory<byte>, DateTimeOffset?> deserializer)
    {
        if (timestampPropertyName is null || userProperties is null)
        {
            return null;
        }

        foreach (var property in userProperties)
        {
            if (property.Name == timestampPropertyName)
            {
                try
                {
                    return deserializer(property.ValueBuffer);
                }
                catch (Exception)
                {
                    // Invalid optional metadata must not prevent delivery of the MQTT value.
                    return null;
                }
            }
        }

        return null;
    }

    public static string BuildTopic(string path, string? topicPrefix)
    {
        return topicPrefix is null
            ? path
            : string.Concat(topicPrefix, "/", path);
    }

    public static string StripTopicPrefix(string topic, string? topicPrefix)
    {
        if (topicPrefix is not null &&
            topic.StartsWith(topicPrefix, StringComparison.Ordinal) &&
            topic.Length > topicPrefix.Length &&
            topic[topicPrefix.Length] == '/')
        {
            return topic.Substring(topicPrefix.Length + 1);
        }

        return topic;
    }
}

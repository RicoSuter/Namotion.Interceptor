using System.Buffers.Text;

namespace Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;

/// <summary>
/// Tick-precision UTC timestamp serialization for MQTT source timestamps. Defaults to
/// Unix milliseconds in production; the connector tester uses ticks so snapshot comparison
/// observes the same timestamp on both sides without millisecond rounding loss.
/// </summary>
public static class MqttTickTimestampCodec
{
    public static byte[] Serialize(DateTimeOffset timestamp)
    {
        Span<byte> buffer = stackalloc byte[20];
        Utf8Formatter.TryFormat(timestamp.UtcTicks, buffer, out var bytesWritten);
        return buffer[..bytesWritten].ToArray();
    }

    public static DateTimeOffset? Deserialize(ReadOnlyMemory<byte> value)
    {
        return Utf8Parser.TryParse(value.Span, out long ticks, out _)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
    }
}

using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>Writes values in the matterjs-server JSON form, where struct fields are keyed by name.</summary>
internal sealed class JsonMatterValueWriter(Utf8JsonWriter writer) : IMatterValueWriter
{
    /// <summary>Writes a property with a payload as its value; a null payload is written as an empty struct.</summary>
    /// <exception cref="ArgumentException">The payload left a struct or list open, or closed one it did not open.</exception>
    public static void WriteProperty(Utf8JsonWriter writer, string propertyName, IMatterPayload? payload)
    {
        writer.WritePropertyName(propertyName);
        var depth = writer.CurrentDepth;
        (payload ?? MatterPayload.Empty).Write(new JsonMatterValueWriter(writer));
        if (writer.CurrentDepth != depth)
        {
            throw new ArgumentException("The payload left a struct or list open, or closed one it did not open.", nameof(payload));
        }
    }

    public void WriteNull() => writer.WriteNullValue();

    public void WriteBoolean(bool value) => writer.WriteBooleanValue(value);

    public void WriteInt64(long value) => writer.WriteNumberValue(value);

    public void WriteUInt64(ulong value) => writer.WriteNumberValue(value);

    public void WriteDouble(double value) => writer.WriteNumberValue(value);

    public void WriteString(string value) => writer.WriteStringValue(value);

    public void WriteBytes(ReadOnlySpan<byte> value) => writer.WriteBase64StringValue(value);

    public void WriteStartStruct() => writer.WriteStartObject();

    public void WriteEndStruct() => writer.WriteEndObject();

    public void WriteStartList() => writer.WriteStartArray();

    public void WriteEndList() => writer.WriteEndArray();

    public void WriteFieldName(uint tag, string name) => writer.WritePropertyName(name);
}

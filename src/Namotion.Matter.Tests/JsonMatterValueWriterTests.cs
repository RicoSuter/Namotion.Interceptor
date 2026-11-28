using System.Buffers;
using System.Text;
using System.Text.Json;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests;

public class JsonMatterValueWriterTests
{
    private static string Write(IMatterPayload payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            payload.Write(new JsonMatterValueWriter(jsonWriter));
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void WhenWritingStruct_ThenFieldsAreKeyedByName()
    {
        // Arrange
        var payload = MatterPayload.Create(writer =>
        {
            writer.WriteStartStruct();
            writer.WriteFieldName(0, "level");
            writer.WriteUInt64(128);
            writer.WriteFieldName(1, "transitionTime");
            writer.WriteNull();
            writer.WriteEndStruct();
        });

        // Act
        var json = Write(payload);

        // Assert
        Assert.Equal("{\"level\":128,\"transitionTime\":null}", json);
    }

    [Fact]
    public void WhenWritingListOfPrimitives_ThenJsonArrayIsWritten()
    {
        // Arrange
        var payload = MatterPayload.Create(writer =>
        {
            writer.WriteStartList();
            writer.WriteBoolean(true);
            writer.WriteInt64(-5);
            writer.WriteDouble(1.5);
            writer.WriteString("a");
            writer.WriteBytes([1, 2, 3]);
            writer.WriteEndList();
        });

        // Act
        var json = Write(payload);

        // Assert
        Assert.Equal("[true,-5,1.5,\"a\",\"AQID\"]", json);
    }

    [Fact]
    public void WhenWritingEmptyPayload_ThenEmptyStructIsWritten()
    {
        // Act
        var json = Write(MatterPayload.Empty);

        // Assert
        Assert.Equal("{}", json);
    }
}

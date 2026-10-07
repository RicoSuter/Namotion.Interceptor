using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// Writes the registers of a chain as JSON, the fixture format the tests replay.
/// </summary>
internal static class SunSpecRegisterDump
{
    public static string Write(byte unitId, SunSpecChain chain)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("unitId", unitId);
            writer.WriteNumber("markerAddress", chain.MarkerAddress);
            writer.WriteStartArray("models");
            foreach (var model in chain.Models)
            {
                writer.WriteStartObject();
                writer.WriteNumber("modelId", model.ModelId);
                writer.WriteNumber("address", model.Address);
                writer.WriteStartArray("registers");
                foreach (var register in model.Registers)
                {
                    writer.WriteNumberValue(register);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

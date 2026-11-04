using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// The scale factor of a point: the name of a <c>sunssf</c> point, a fixed power-of-ten exponent, or none.
/// </summary>
internal readonly record struct SunSpecScaleFactor(string? PointName, int? Exponent);

internal sealed class SunSpecScaleFactorJsonConverter : JsonConverter<SunSpecScaleFactor>
{
    public override SunSpecScaleFactor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new SunSpecScaleFactor(reader.GetString(), null),
        JsonTokenType.Number => new SunSpecScaleFactor(null, reader.GetInt32()),
        JsonTokenType.Null => default,
        _ => throw new JsonException($"A scale factor must be a point name or a number, not {reader.TokenType}.")
    };

    public override void Write(Utf8JsonWriter writer, SunSpecScaleFactor value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

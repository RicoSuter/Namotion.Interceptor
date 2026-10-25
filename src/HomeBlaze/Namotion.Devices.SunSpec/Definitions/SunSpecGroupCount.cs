using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// How often a group occurs: once (no count), a fixed number of times, as often as it fits the model length
/// (<see cref="IsFill"/>, count 0), or as often as the count point <see cref="PointName"/> says.
/// </summary>
internal readonly record struct SunSpecGroupCount(int? Fixed, string? PointName)
{
    public bool IsSingle => Fixed is null && PointName is null;

    public bool IsFill => Fixed == 0;
}

internal sealed class SunSpecGroupCountJsonConverter : JsonConverter<SunSpecGroupCount>
{
    public override SunSpecGroupCount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => new SunSpecGroupCount(reader.GetInt32(), null),
        JsonTokenType.String => new SunSpecGroupCount(null, reader.GetString()),
        JsonTokenType.Null => default,
        _ => throw new JsonException($"A group count must be a number or a point name, not {reader.TokenType}.")
    };

    public override void Write(Utf8JsonWriter writer, SunSpecGroupCount value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}

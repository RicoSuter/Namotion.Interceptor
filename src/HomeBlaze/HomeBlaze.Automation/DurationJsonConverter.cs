using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeBlaze.Automation;

/// <summary>
/// Writes a duration as automation expressions do, such as <c>10m</c> or <c>1h30m</c>, and reads that form and the
/// form of <see cref="TimeSpan"/>, such as <c>00:10:00</c>. A number or a text without a unit is no duration.
/// </summary>
internal sealed class DurationJsonConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (AutomationDuration.TryParse(text, out var duration) ||
            (text is not null && text.Contains(':') && TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out duration)))
        {
            return duration;
        }

        throw new JsonException("A duration is a text such as 10m, 1h30m or 00:10:00.");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is { } duration)
        {
            writer.WriteStringValue(AutomationDuration.Format(duration));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client;

/// <summary>
/// Reads Tesla numbers and booleans leniently: a value of an unexpected shape (a placeholder text, an object, an out-of-range number)
/// becomes null instead of failing the whole document. Numeric texts are accepted.
/// </summary>
internal static class TeslaLenientReader
{
    public static decimal? ReadDecimal(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number) ? number : null;

            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

            default:
                SkipValue(ref reader);
                return null;
        }
    }

    public static long? ReadInteger(ref Utf8JsonReader reader, long minimum, long maximum)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var exact))
        {
            return exact >= minimum && exact <= maximum ? exact : null;
        }

        var value = ReadDecimal(ref reader);
        if (value is null)
        {
            return null;
        }

        var rounded = Math.Round(value.Value, MidpointRounding.AwayFromZero);
        return rounded >= minimum && rounded <= maximum ? (long)rounded : null;
    }

    public static bool? ReadBoolean(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
                return false;

            case JsonTokenType.String:
                return bool.TryParse(reader.GetString(), out var parsed) ? parsed : null;

            default:
                SkipValue(ref reader);
                return null;
        }
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }
}

internal sealed class LenientInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (int?)TeslaLenientReader.ReadInteger(ref reader, int.MinValue, int.MaxValue);

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class LenientInt64Converter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadInteger(ref reader, long.MinValue, long.MaxValue);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class LenientDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadDecimal(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

internal sealed class LenientBooleanConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadBoolean(ref reader);

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteBooleanValue(value.Value);
        }
    }
}

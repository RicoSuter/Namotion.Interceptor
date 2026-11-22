using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>Writes request bodies and reads values by dotted path, without reflection serialization.</summary>
internal static class BluelinkJson
{
    /// <summary>Writes a JSON object with the given members, compact.</summary>
    public static string Write(Action<Utf8JsonWriter> writeMembers)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeMembers(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Returns the element at a dotted path such as <c>Green.ChargingInformation.TargetSoC.Standard</c>, or null.</summary>
    public static JsonElement? Find(this JsonElement element, string path)
    {
        var current = element;
        foreach (var name in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    /// <summary>Reads a number or a numeric string; null when missing or not numeric.</summary>
    public static decimal? GetDecimal(this JsonElement element, string path) => element.Find(path) switch
    {
        { ValueKind: JsonValueKind.Number } number when number.TryGetDecimal(out var value) => value,
        { ValueKind: JsonValueKind.String } text when decimal.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
        _ => null
    };

    public static int? GetInt32(this JsonElement element, string path) =>
        element.GetDecimal(path) is { } value && value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    /// <summary>Reads a string, or a number as invariant text; null when missing.</summary>
    public static string? GetString(this JsonElement element, string path) => element.Find(path) switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        _ => null
    };

    /// <summary>Reads 0 or 1 (or false and true) as a flag; null when missing or another value.</summary>
    public static bool? GetFlag(this JsonElement element, string path) => element.Find(path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        var other when other is not null && element.GetInt32(path) is { } value => value switch { 0 => false, 1 => true, _ => null },
        _ => null
    };
}

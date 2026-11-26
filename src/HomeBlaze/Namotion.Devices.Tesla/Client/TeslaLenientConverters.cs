using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Tesla.Mapping;

namespace Namotion.Devices.Tesla.Client;

/// <summary>
/// Reads Tesla numbers and booleans leniently: a value of an unexpected shape (a placeholder text, an object, an out-of-range number)
/// becomes null instead of failing the whole document. Numeric texts are accepted. Such a value, except the placeholders Tesla
/// sends routinely, is logged once per target type and value as a warning.
/// </summary>
internal static class TeslaLenientReader
{
    private const int MaximumLoggedLength = 40;

    // Process-wide, keyed by token type rather than by value so that a value that changes on every poll is logged only once.
    private static readonly ConcurrentDictionary<(string TargetType, JsonTokenType TokenType), byte> LoggedUnexpectedValues = new();

    public static decimal? ReadDecimal(ref Utf8JsonReader reader, string targetType, ILogger logger)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetDecimal(out var number):
                return number;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                LogUnexpected(ref reader, targetType, logger, text);
                return null;

            default:
                LogUnexpected(ref reader, targetType, logger, null);
                return null;
        }
    }

    public static long? ReadInteger(ref Utf8JsonReader reader, long minimum, long maximum, string targetType, ILogger logger)
    {
        decimal? value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var exact))
        {
            value = exact;
        }
        else
        {
            value = ReadDecimal(ref reader, targetType, logger);
            if (value is null)
            {
                return null;
            }

            value = Math.Round(value.Value, MidpointRounding.AwayFromZero);
        }

        if (value >= minimum && value <= maximum)
        {
            return (long)value.Value;
        }

        LogUnexpected(ref reader, targetType, logger, null);
        return null;
    }

    public static bool? ReadBoolean(ref Utf8JsonReader reader, ILogger logger)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
                return false;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (bool.TryParse(text, out var parsed))
                {
                    return parsed;
                }

                LogUnexpected(ref reader, "bool", logger, text);
                return null;

            default:
                LogUnexpected(ref reader, "bool", logger, null);
                return null;
        }
    }

    /// <summary>
    /// Logs the first unexpected value per target and token type, except a placeholder, and skips it.
    /// <paramref name="text"/> is the value of a string token when it was already read; only a numeric text is not passed.
    /// </summary>
    private static void LogUnexpected(ref Utf8JsonReader reader, string targetType, ILogger logger, string? text)
    {
        var isPlaceholder = text is not null && TeslaValues.Text(text) is null;
        if (!isPlaceholder && logger.IsEnabled(LogLevel.Warning) && LoggedUnexpectedValues.TryAdd((targetType, reader.TokenType), 0))
        {
            logger.LogWarning("Tesla API changed? Unexpected {TargetType} value {Value}.", targetType, Describe(ref reader, text));
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static string Describe(ref Utf8JsonReader reader, string? text)
    {
        var value = reader.TokenType switch
        {
            JsonTokenType.String => text ?? reader.GetString()!,
            JsonTokenType.Number when reader.TryGetDouble(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => reader.TokenType.ToString()
        };

        return value.Length > MaximumLoggedLength ? value[..MaximumLoggedLength] : value;
    }
}

/// <summary>A converter of the Tesla JSON context, which only reads.</summary>
internal abstract class LenientReadOnlyConverter<T> : JsonConverter<T>
{
    /// <summary>Receives a warning for each value of an unexpected type.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("The Tesla JSON context only reads.");
}

internal sealed class LenientInt32Converter : LenientReadOnlyConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (int?)TeslaLenientReader.ReadInteger(ref reader, int.MinValue, int.MaxValue, "int", Logger);
}

internal sealed class LenientInt64Converter : LenientReadOnlyConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadInteger(ref reader, long.MinValue, long.MaxValue, "long", Logger);
}

internal sealed class LenientDecimalConverter : LenientReadOnlyConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadDecimal(ref reader, "decimal", Logger);
}

internal sealed class LenientBooleanConverter : LenientReadOnlyConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TeslaLenientReader.ReadBoolean(ref reader, Logger);
}

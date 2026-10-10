using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Converts JSON values sent by an agent, such as property values and method arguments, to CLR values.
/// </summary>
public static class McpValueConverter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="type"/>. A type listed as a JSON Schema string, such as
    /// <see cref="TimeSpan"/>, <see cref="DateTime"/>, <see cref="Guid"/> or an enum, is read from its string form.
    /// Any other type may also arrive as a string holding its JSON, such as <c>"0.5"</c> or <c>"true"</c>, which some
    /// clients send. An enum accepts a defined name, ignoring case, or a defined number; a <see cref="FlagsAttribute"/>
    /// enum also accepts combinations.
    /// </summary>
    /// <exception cref="JsonException">The value cannot be converted to <paramref name="type"/>.</exception>
    public static object? Deserialize(JsonElement value, Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);
        var valueType = underlyingType ?? type;
        if (valueType.IsEnum)
        {
            return value.ValueKind == JsonValueKind.Null && underlyingType is not null
                ? null
                : DeserializeEnum(value, valueType);
        }

        if (value.ValueKind == JsonValueKind.String && !JsonSchemaTypeMapper.IsWrittenAsString(valueType) && valueType != typeof(object))
        {
            return JsonSerializer.Deserialize(value.GetString()!, type, SerializerOptions);
        }

        return value.Deserialize(type, SerializerOptions);
    }

    private static object DeserializeEnum(JsonElement value, Type enumType)
    {
        var isFlags = enumType.IsDefined(typeof(FlagsAttribute), inherit: false);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (isFlags)
            {
                if (Enum.TryParse(enumType, text, ignoreCase: true, out var flags))
                {
                    return flags;
                }
            }
            else
            {
                // Enum.TryParse would also take numbers and comma lists, which a non-flags enum must reject.
                foreach (var name in Enum.GetNames(enumType))
                {
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    {
                        return Enum.Parse(enumType, name);
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            object? number = value.TryGetInt64(out var signed) ? Enum.ToObject(enumType, signed)
                : value.TryGetUInt64(out var unsigned) ? Enum.ToObject(enumType, unsigned)
                : null;
            if (number is not null && (isFlags || Enum.IsDefined(enumType, number)))
            {
                return number;
            }
        }

        throw new JsonException(
            $"The value {value.GetRawText()} is not a valid {enumType.Name}. Allowed values: {string.Join(", ", Enum.GetNames(enumType))}.");
    }
}

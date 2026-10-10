namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Maps CLR types to JSON Schema type strings.
/// </summary>
public static class JsonSchemaTypeMapper
{
    public static string? ToJsonSchemaType(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (IsWrittenAsString(type))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
        {
            return "integer";
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return "number";
        }

        if (type.IsArray || (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
        {
            return "array";
        }

        return "object";
    }

    /// <summary>
    /// Gets the format label of the string a value of <paramref name="type"/> is written and read as by System.Text.Json:
    /// the standard JSON Schema format name where one matches (<c>date-time</c>, <c>date</c>, <c>uuid</c>, <c>uri</c>),
    /// otherwise a short label (<c>hh:mm:ss</c> for <see cref="TimeSpan"/>, <c>HH:mm:ss</c> for
    /// <see cref="TimeOnly"/>, <c>base64</c> for a byte array). Null when the type has no format.
    /// </summary>
    internal static string? GetFormat(Type? type)
    {
        type = UnwrapNullable(type);
        return type == typeof(DateTime) || type == typeof(DateTimeOffset) ? "date-time"
            : type == typeof(DateOnly) ? "date"
            : type == typeof(Guid) ? "uuid"
            : type == typeof(Uri) ? "uri"
            : type == typeof(TimeSpan) ? "hh:mm:ss"
            : type == typeof(TimeOnly) ? "HH:mm:ss"
            : type == typeof(byte[]) ? "base64"
            : null;
    }

    private static Type? UnwrapNullable(Type? type) =>
        type is null ? null : Nullable.GetUnderlyingType(type) ?? type;

    // The types System.Text.Json writes as JSON strings by default; enums are listed and read by name. Every type
    // with a format label is one of them, so GetFormat stays the single list of those.
    internal static bool IsWrittenAsString(Type type) =>
        type == typeof(string) || type == typeof(char) || type.IsEnum || GetFormat(type) is not null;
}

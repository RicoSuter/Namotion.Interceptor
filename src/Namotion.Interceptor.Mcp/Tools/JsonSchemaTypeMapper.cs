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
    /// Gets the standard JSON Schema format name of the string a value of <paramref name="type"/> is written as by
    /// System.Text.Json: <c>date-time</c>, <c>date</c>, <c>uuid</c> or <c>uri</c>. Null when no standard format
    /// matches; <see cref="GetPattern"/> covers <see cref="TimeSpan"/> and <see cref="TimeOnly"/>.
    /// </summary>
    public static string? GetFormat(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "date-time";
        }

        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(Guid))
        {
            return "uuid";
        }

        if (type == typeof(Uri))
        {
            return "uri";
        }

        return null;
    }

    /// <summary>
    /// Gets a JSON Schema <c>pattern</c> (a regular expression) for the string a value of <paramref name="type"/> is
    /// written and read as by System.Text.Json, for types without a standard format: <see cref="TimeSpan"/>
    /// (<c>[-][d.]hh:mm:ss[.fffffff]</c>) and <see cref="TimeOnly"/> (<c>HH:mm:ss[.fffffff]</c>). Null otherwise.
    /// </summary>
    public static string? GetPattern(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(TimeSpan))
        {
            return @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$";
        }

        if (type == typeof(TimeOnly))
        {
            return @"^\d{2}:\d{2}:\d{2}(\.\d{1,7})?$";
        }

        return null;
    }

    // The types System.Text.Json writes as JSON strings by default; enums are listed and read by name.
    internal static bool IsWrittenAsString(Type type) =>
        type == typeof(string) || type == typeof(char) || type.IsEnum ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
        type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri);
}

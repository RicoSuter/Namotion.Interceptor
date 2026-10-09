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

        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            type = underlying;
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(Uri) || GetFormat(type) is not null)
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

        if (type.IsEnum)
        {
            return "string";
        }

        if (type.IsArray || (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
        {
            return "array";
        }

        return "object";
    }

    /// <summary>
    /// Gets the string format a value of <paramref name="type"/> is written and read in by System.Text.Json:
    /// a JSON Schema format name (<c>date-time</c>, <c>date</c>, <c>uuid</c>, <c>uri</c>) or, for
    /// <see cref="TimeSpan"/> and <see cref="TimeOnly"/>, the pattern. Null when the type has no string format.
    /// </summary>
    public static string? GetFormat(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(TimeSpan))
        {
            return "[d.]hh:mm:ss[.fffffff]";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "date-time";
        }

        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(TimeOnly))
        {
            return "HH:mm:ss[.fffffff]";
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
}

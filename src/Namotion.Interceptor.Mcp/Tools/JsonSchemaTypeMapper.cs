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

        if (type == typeof(string) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid))
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
}

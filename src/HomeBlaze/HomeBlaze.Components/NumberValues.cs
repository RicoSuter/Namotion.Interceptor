using System.Globalization;

namespace HomeBlaze.Components;

/// <summary>
/// Reads and writes the numbers that widgets show and set.
/// </summary>
internal static class NumberValues
{
    /// <summary>
    /// Gets whether <see cref="TryGetNumber"/> reads values of the type, a nullable type as its underlying type.
    /// </summary>
    public static bool IsNumberType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        // An enum has the type code of its underlying integral type.
        return !type.IsEnum && Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;
    }

    /// <summary>
    /// Gets a value of a numeric type as a double. False for null, for a value of another type and for NaN; an
    /// infinity is a number.
    /// </summary>
    public static bool TryGetNumber(object? value, out double number)
    {
        switch (value)
        {
            case double doubleValue:
                number = doubleValue;
                break;
            case float floatValue:
                number = floatValue;
                break;
            case decimal decimalValue:
                number = (double)decimalValue;
                break;
            case int intValue:
                number = intValue;
                break;
            case long longValue:
                number = longValue;
                break;
            case short shortValue:
                number = shortValue;
                break;
            case byte byteValue:
                number = byteValue;
                break;
            case sbyte sbyteValue:
                number = sbyteValue;
                break;
            case uint uintValue:
                number = uintValue;
                break;
            case ulong ulongValue:
                number = ulongValue;
                break;
            case ushort ushortValue:
                number = ushortValue;
                break;
            default:
                number = 0;
                return false;
        }

        return !double.IsNaN(number);
    }

    /// <summary>
    /// Writes a number for a message: invariant, with up to ten decimals.
    /// </summary>
    public static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}

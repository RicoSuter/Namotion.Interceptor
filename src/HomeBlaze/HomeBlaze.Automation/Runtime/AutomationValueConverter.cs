using System.Globalization;
using HomeBlaze.Services.Display;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Converts the value of an expression to the type of a parameter or property: a value of the type as it is, a text or
/// an enum to an enum by name, a number to another number type, and anything to a text as pages write it raw.
/// </summary>
internal static class AutomationValueConverter
{
    public static bool TryConvert(object value, Type targetType, out object? converted)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (type.IsEnum)
        {
            return TryConvertEnum(value, type, out converted);
        }

        if (type == typeof(string))
        {
            converted = RawValueFormatter.Format(value);
            return true;
        }

        if (IsNumber(type) && IsNumber(value.GetType()))
        {
            return TryChangeType(value, type, out converted);
        }

        converted = null;
        return false;
    }

    private static bool TryConvertEnum(object value, Type type, out object? converted)
    {
        var name = value is string or Enum ? value.ToString() : null;
        if (name is not null && Enum.TryParse(type, name, ignoreCase: true, out var parsed))
        {
            converted = parsed;
            return true;
        }

        converted = null;
        return false;
    }

    private static bool IsNumber(Type type) => !type.IsEnum && Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;

    private static bool TryChangeType(object value, Type type, out object? converted)
    {
        try
        {
            converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            converted = null;
            return false;
        }
    }
}

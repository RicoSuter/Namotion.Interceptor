using System.Globalization;

namespace HomeBlaze.Virtual;

/// <summary>Converts the values a virtual property reads to its data type, and the values it writes to its target.</summary>
internal static class VirtualValueConverter
{
    private static readonly HashSet<Type> IntegerTypes =
    [
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong)
    ];

    private static readonly HashSet<Type> NumberTypes =
    [
        typeof(decimal), typeof(double), typeof(float), .. IntegerTypes
    ];

    /// <summary>Converts a value to the data type; null when it is null or does not convert.</summary>
    public static object? ToDataType(object? value, VirtualDataType dataType) => dataType switch
    {
        VirtualDataType.Number => ToNumber(value),
        VirtualDataType.Boolean => value is bool ? value : null,
        VirtualDataType.Text => ToText(value),
        VirtualDataType.Duration => value is TimeSpan ? value : null,
        VirtualDataType.DateTime => ToInstant(value),
        _ => null
    };

    /// <summary>Converts a value to the type of a property it is written to; false when it does not convert.</summary>
    public static bool TryConvertToTarget(object? value, Type targetType, out object? converted)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        converted = null;
        if (value is null)
        {
            return !targetType.IsValueType || underlying != targetType;
        }

        if (underlying.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        try
        {
            return TryConvertNonAssignable(value, underlying, out converted);
        }
        catch (OverflowException)
        {
            // Out of the range of the target type.
            return false;
        }
    }

    private static bool TryConvertNonAssignable(object value, Type underlying, out object? converted)
    {
        converted = null;
        if (underlying.IsEnum)
        {
            return value is string name && TryParseEnum(name, underlying, out converted);
        }

        if (underlying == typeof(string))
        {
            converted = ToText(value);
            return true;
        }

        if (underlying == typeof(DateTime) && value is DateTimeOffset instant)
        {
            converted = instant.UtcDateTime;
            return true;
        }

        if (IsNumber(underlying) && IsNumber(value.GetType()))
        {
            // Convert.ChangeType rounds a fraction to an integer, which would write another value than the one given.
            if (IntegerTypes.Contains(underlying) && !IsIntegral(value))
            {
                return false;
            }

            converted = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    // By the names of its values only: a number, or names combined for an enum without [Flags], would write a value that
    // the enum may not have.
    private static bool TryParseEnum(string text, Type enumType, out object? converted)
    {
        converted = null;
        var isFlags = enumType.IsDefined(typeof(FlagsAttribute), inherit: false);
        if ((!isFlags && text.Contains(',')) || !HasNamesOnly(text))
        {
            return false;
        }

        if (!Enum.TryParse(enumType, text, ignoreCase: true, out var parsed) ||
            (!isFlags && !Enum.IsDefined(enumType, parsed)))
        {
            return false;
        }

        converted = parsed;
        return true;
    }

    private static bool HasNamesOnly(string text)
    {
        foreach (var range in text.AsSpan().Split(','))
        {
            var part = text.AsSpan(range).Trim();
            if (part.IsEmpty || char.IsAsciiDigit(part[0]) || part[0] is '-' or '+')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIntegral(object number) => number switch
    {
        decimal value => decimal.IsInteger(value),
        double value => double.IsInteger(value),
        float value => float.IsInteger(value),
        _ => true
    };

    /// <summary>Gets whether the type is one of the CLR number types that expressions compute with.</summary>
    public static bool IsNumber(Type type) => NumberTypes.Contains(type);

    private static object? ToNumber(object? value)
    {
        try
        {
            return value switch
            {
                decimal => value,
                double number => double.IsFinite(number) ? (decimal)number : null,
                float number => float.IsFinite(number) ? (decimal)number : null,
                _ when value is not null && IntegerTypes.Contains(value.GetType()) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? ToText(object? value) => value switch
    {
        null => null,
        string text => text,
        Enum enumValue => enumValue.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static object? ToInstant(object? value)
    {
        try
        {
            return value switch
            {
                DateTimeOffset => value,
                DateTime { Kind: DateTimeKind.Unspecified } dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
                DateTime dateTime => new DateTimeOffset(dateTime),
                _ => null
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // A local time whose UTC is outside the range of DateTime has no instant.
            return null;
        }
    }
}

using System.ComponentModel;
using System.Globalization;

namespace HomeBlaze.Services;

/// <summary>
/// Utilities for converting and validating method parameters.
/// </summary>
public static class ParameterConverter
{
    private static readonly HashSet<Type> SupportedPrimitives =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(float),
        typeof(decimal),
        typeof(bool),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Guid),
        typeof(TimeSpan)
    ];

    /// <summary>
    /// Checks if a type is supported for UI parameter input.
    /// </summary>
    public static bool IsSupported(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return SupportedPrimitives.Contains(underlyingType) || underlyingType.IsEnum;
    }

    /// <summary>
    /// Tries to convert a string input to the target type.
    /// </summary>
    public static bool TryConvert(string? input, Type targetType, out object? value)
    {
        value = null;

        var underlyingType = Nullable.GetUnderlyingType(targetType);
        var isNullable = underlyingType != null;
        var actualType = underlyingType ?? targetType;

        // Handle null/empty for nullable types
        if (string.IsNullOrEmpty(input))
        {
            if (isNullable || actualType == typeof(string))
            {
                value = null;
                return true;
            }
            return false;
        }

        try
        {
            // Handle enums
            if (actualType.IsEnum)
            {
                if (Enum.TryParse(actualType, input, ignoreCase: true, out var enumValue))
                {
                    value = enumValue;
                    return true;
                }
                return false;
            }

            // Handle specific types
            if (actualType == typeof(string))
            {
                value = input;
                return true;
            }

            if (actualType == typeof(bool))
            {
                if (bool.TryParse(input, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }
                return false;
            }

            if (actualType == typeof(Guid))
            {
                if (Guid.TryParse(input, out var guidValue))
                {
                    value = guidValue;
                    return true;
                }
                return false;
            }

            if (actualType == typeof(TimeSpan))
            {
                if (TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out var timeSpanValue))
                {
                    value = timeSpanValue;
                    return true;
                }
                return false;
            }

            // Use TypeConverter for numeric types and DateTime
            var converter = TypeDescriptor.GetConverter(actualType);
            if (converter.CanConvertFrom(typeof(string)))
            {
                value = converter.ConvertFromInvariantString(input);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

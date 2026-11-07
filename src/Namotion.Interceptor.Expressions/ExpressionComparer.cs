using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Compares two values that are not null: a boolean only for equality, an enum by its name without regard to case and
/// only for equality, a text without regard to case for equality and ordinal for order, a date against ISO 8601 text,
/// a duration against a duration, a float as a float, and NaN never. A null result means the pair has no order, so
/// the comparison is unknown.
/// </summary>
internal static class ExpressionComparer
{
    /// <summary>Compares two values that are not null.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="leftInstant">The instant of the left operand when it is a text literal that is a date, otherwise null.</param>
    /// <param name="right">The right value.</param>
    /// <param name="rightInstant">The instant of the right operand when it is a text literal that is a date, otherwise null.</param>
    /// <param name="isEquality">Whether the comparison is <c>==</c> or <c>!=</c>.</param>
    /// <returns>The order of the left value relative to the right one, or null when their types cannot be compared.</returns>
    public static int? Compare(object left, DateTimeOffset? leftInstant, object right, DateTimeOffset? rightInstant, bool isEquality)
    {
        if (IsNaN(left) || IsNaN(right))
        {
            return null;
        }

        // The pairs below have the richer type on the left; the reverse order negates the result.
        return CompareOrdered(left, right, rightInstant, isEquality) ?? -CompareOrdered(right, left, leftInstant, isEquality);
    }

    private static int? CompareOrdered(object value, object other, DateTimeOffset? otherInstant, bool isEquality) => value switch
    {
        bool boolean => CompareBoolean(boolean, other, isEquality),
        Enum enumValue => CompareEnum(enumValue, other, isEquality),
        string text => CompareText(text, other, isEquality),
        TimeSpan duration => other is TimeSpan otherDuration ? duration.CompareTo(otherDuration) : null,
        _ when TryGetInstant(value, out var instant) => CompareInstant(instant, other, otherInstant),
        _ => CompareNumber(value, other)
    };

    private static int? CompareBoolean(bool boolean, object other, bool isEquality)
    {
        if (isEquality && other is bool otherBoolean)
        {
            return boolean == otherBoolean ? 0 : 1;
        }

        return null;
    }

    private static int? CompareEnum(Enum enumValue, object other, bool isEquality)
    {
        if (other is string name)
        {
            return isEquality ? CompareEquality(GetEnumName(enumValue), name) : null;
        }

        if (other is Enum otherEnum && enumValue.GetType() == otherEnum.GetType())
        {
            return enumValue.CompareTo(otherEnum);
        }

        return null;
    }

    private static int? CompareText(string text, object other, bool isEquality)
    {
        if (other is not string otherText)
        {
            return null;
        }

        return isEquality ? CompareEquality(text, otherText) : string.CompareOrdinal(text, otherText);
    }

    private static int CompareEquality(string text, string otherText) =>
        string.Equals(text, otherText, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int? CompareInstant(DateTimeOffset instant, object other, DateTimeOffset? otherInstant)
    {
        if (other is string)
        {
            return otherInstant is { } literalInstant ? instant.CompareTo(literalInstant) : null;
        }

        return TryGetInstant(other, out var otherValue) ? instant.CompareTo(otherValue) : null;
    }

    private static int? CompareNumber(object value, object other)
    {
        // Widening a float to double would make 0.1f differ from the literal 0.1.
        if (value is float single && other is double floatNumber)
        {
            return single.CompareTo((float)floatNumber);
        }

        if (value is double number && other is float otherSingle)
        {
            return ((float)number).CompareTo(otherSingle);
        }

        if (!IsNumber(value) || !IsNumber(other))
        {
            return null;
        }

        return ToDouble(value).CompareTo(ToDouble(other));
    }

    // The cached name of a defined value, so a comparison does not allocate; ToString only for flag combinations and
    // undefined values.
    private static string GetEnumName(Enum enumValue) =>
        Enum.GetName(enumValue.GetType(), enumValue) ?? enumValue.ToString();

    internal static bool IsNumber(object value) => value is float or double || IsIntegerOrDecimal(value);

    internal static bool IsIntegerOrDecimal(object value) => value is decimal || IsSignedInteger(value) || IsUnsignedInteger(value);

    internal static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    internal static decimal ToDecimal(object value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool IsSignedInteger(object value) => value is sbyte or short or int or long;

    private static bool IsUnsignedInteger(object value) => value is byte or ushort or uint or ulong;

    private static bool IsNaN(object value) =>
        (value is double number && double.IsNaN(number)) || (value is float single && float.IsNaN(single));

    /// <summary>Gets the instant of a <see cref="DateTimeOffset"/> or <see cref="DateTime"/>.</summary>
    /// <returns>False for another value, or a local time whose UTC is out of the range of <see cref="DateTime"/>.</returns>
    internal static bool TryGetInstant(object value, out DateTimeOffset instant)
    {
        switch (value)
        {
            case DateTimeOffset dateTimeOffset:
                instant = dateTimeOffset;
                return true;
            case DateTime dateTime:
                return TryConvertToInstant(dateTime, out instant);
            default:
                instant = default;
                return false;
        }
    }

    // A DateTime without a kind is read as UTC, matching how a text without an offset is read.
    private static bool TryConvertToInstant(DateTime dateTime, out DateTimeOffset instant)
    {
        var offset = dateTime.Kind == DateTimeKind.Local ? TimeZoneInfo.Local.GetUtcOffset(dateTime) : TimeSpan.Zero;
        var utcTicks = dateTime.Ticks - offset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
        {
            instant = default;
            return false;
        }

        instant = new DateTimeOffset(dateTime.Ticks, offset);
        return true;
    }
}

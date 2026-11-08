using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>Compares two values that are not null, as docs/expressions.md#comparisons describes; a null result means the pair has no order, so the comparison is unknown.</summary>
internal static class ExpressionComparer
{
    /// <summary>Compares two values that are not null.</summary>
    /// <param name="left">The left value.</param>
    /// <param name="leftLiteral">The conversions of the left operand when it is a literal, otherwise the default.</param>
    /// <param name="right">The right value.</param>
    /// <param name="rightLiteral">The conversions of the right operand when it is a literal, otherwise the default.</param>
    /// <param name="isEquality">Whether the comparison is <c>==</c> or <c>!=</c>.</param>
    /// <returns>The order of the left value relative to the right one, or null when their types cannot be compared.</returns>
    public static int? Compare(object left, in ConvertedLiteral leftLiteral, object right, in ConvertedLiteral rightLiteral, bool isEquality)
    {
        if (IsNaN(left) || IsNaN(right))
        {
            return null;
        }

        // The pairs below have the richer type on the left; the reverse order negates the result.
        return CompareOrdered(left, leftLiteral, right, rightLiteral, isEquality) ?? -CompareOrdered(right, rightLiteral, left, leftLiteral, isEquality);
    }

    private static int? CompareOrdered(object value, in ConvertedLiteral literal, object other, in ConvertedLiteral otherLiteral, bool isEquality) => value switch
    {
        bool boolean => CompareBoolean(boolean, other, isEquality),
        Enum enumValue => CompareEnum(enumValue, other, isEquality),
        string text => CompareText(text, other, isEquality),
        TimeSpan duration => other is TimeSpan otherDuration ? duration.CompareTo(otherDuration) : null,
        _ when TryGetInstant(value, out var instant) => CompareInstant(instant, other, otherLiteral.Instant),
        _ => CompareNumber(value, literal.Number, other, otherLiteral.Number)
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

    // Widening a float would make 0.1f differ from the literal 0.1, so a float compares with a double or decimal as a
    // float, and with an integer or another float as a double. A literal brings its double along, so a value compares
    // with it without converting the literal again.
    private static int? CompareNumber(object value, double? number, object other, double? otherNumber)
    {
        var kind = GetNumberKind(value);
        var otherKind = GetNumberKind(other);
        if (kind == NumberKind.None || otherKind == NumberKind.None)
        {
            return null;
        }

        return (kind | otherKind) switch
        {
            NumberKind.Float | NumberKind.Double or NumberKind.Float | NumberKind.Decimal => ((float)ToDouble(value, number)).CompareTo((float)ToDouble(other, otherNumber)),
            var kinds when IsExact(kinds) => ToDecimal(value).CompareTo(ToDecimal(other)),
            _ => ToDouble(value, number).CompareTo(ToDouble(other, otherNumber))
        };
    }

    private static double ToDouble(object value, double? converted) => converted ?? ToDouble(value);

    // The cached name of a defined value, so a comparison does not allocate; ToString only for flag combinations and
    // undefined values.
    private static string GetEnumName(Enum enumValue) =>
        Enum.GetName(enumValue.GetType(), enumValue) ?? enumValue.ToString();

    internal static NumberKind GetNumberKind(object value) => value switch
    {
        float => NumberKind.Float,
        double => NumberKind.Double,
        decimal => NumberKind.Decimal,
        _ when IsSignedInteger(value) || IsUnsignedInteger(value) => NumberKind.Integer,
        _ => NumberKind.None
    };

    /// <summary>Whether the kinds of numbers, such as those of two operands combined, are only integers and decimals.</summary>
    internal static bool IsExact(NumberKind kinds) => (kinds & ~(NumberKind.Integer | NumberKind.Decimal)) == 0;

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
                // A DateTime without a kind is read as UTC, matching how a text without an offset is read.
                var offset = dateTime.Kind == DateTimeKind.Local ? TimeZoneInfo.Local.GetUtcOffset(dateTime) : TimeSpan.Zero;
                return TryConvertToInstant(dateTime, offset, out instant);
            default:
                instant = default;
                return false;
        }
    }

    /// <summary>Gets the instant of a date and time at an offset from UTC.</summary>
    /// <returns>False when its UTC is out of the range of <see cref="DateTime"/>.</returns>
    internal static bool TryConvertToInstant(DateTime dateTime, TimeSpan offset, out DateTimeOffset instant)
    {
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

/// <summary>
/// How the comparer and the arithmetic treat a number: every integer type alike, and each other type by itself. The kinds
/// of two operands combine as flags.
/// </summary>
[Flags]
internal enum NumberKind
{
    None = 0,
    Integer = 1,
    Decimal = 2,
    Float = 4,
    Double = 8
}

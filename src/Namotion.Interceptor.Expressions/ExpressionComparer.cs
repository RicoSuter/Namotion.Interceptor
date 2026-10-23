using System.Globalization;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// Compares two values that are not null, with the rules of the where filter of MCP: a boolean only for equality, an enum
/// by its name without regard to case and only for equality, a text without regard to case for equality and ordinal for
/// order, a date against ISO 8601 text, a float as a float, and NaN never. A null result means the pair has no order, so the
/// comparison is unknown.
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

    private static int? CompareOrdered(object value, object other, DateTimeOffset? otherInstant, bool isEquality) => (value, other) switch
    {
        (bool boolean, bool otherBoolean) when isEquality => boolean == otherBoolean ? 0 : 1,
        (Enum enumValue, string name) when isEquality =>
            string.Equals(GetEnumName(enumValue), name, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
        (Enum enumValue, Enum otherEnum) when enumValue.GetType() == otherEnum.GetType() => enumValue.CompareTo(otherEnum),
        (string text, string otherText) => isEquality
            ? string.Equals(text, otherText, StringComparison.OrdinalIgnoreCase) ? 0 : 1
            : string.CompareOrdinal(text, otherText),
        (DateTimeOffset instant, string) => otherInstant is { } literalInstant ? instant.CompareTo(literalInstant) : null,
        (DateTime dateTime, string) => otherInstant is { } literalInstant ? ToInstant(dateTime).CompareTo(literalInstant) : null,
        (DateTimeOffset instant, DateTimeOffset otherDate) => instant.CompareTo(otherDate),
        (DateTime dateTime, DateTime otherDateTime) => ToInstant(dateTime).CompareTo(ToInstant(otherDateTime)),
        // Widening a float to double would make 0.1f differ from the literal 0.1.
        (float single, double number) => single.CompareTo((float)number),
        (double number, float single) => ((float)number).CompareTo(single),
        _ when IsNumber(value) && IsNumber(other) =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(other, CultureInfo.InvariantCulture)),
        _ => null
    };

    // The cached name of a defined value, so a comparison does not allocate; ToString only for flag combinations and
    // undefined values.
    private static string GetEnumName(Enum enumValue) =>
        Enum.GetName(enumValue.GetType(), enumValue) ?? enumValue.ToString();

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool IsNaN(object value) =>
        (value is double number && double.IsNaN(number)) || (value is float single && float.IsNaN(single));

    // A DateTime without a kind is read as UTC, matching how a text without an offset is read.
    private static DateTimeOffset ToInstant(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(dateTime, TimeSpan.Zero)
            : new DateTimeOffset(dateTime);
}

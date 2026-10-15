using System.Globalization;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Converts between the value of a property and the position of a slider. Range and step are in the unit of the
/// property; the slider shows them times the scale.
/// </summary>
public static class SliderMath
{
    private const double PercentScale = 100;
    private const int DefaultStepCount = 100;
    private const int MaximumDecimals = 10;
    private const double WholeTolerance = 1e-9;

    /// <summary>
    /// Gets the factor from the value of the property to the position of the slider: 100 for a property with the unit
    /// percent, which holds 0 to 1 and is shown as 0 to 100, otherwise 1.
    /// </summary>
    public static double GetScale(RegisteredSubjectProperty? property) =>
        property?.GetStateMetadata()?.Unit == StateUnit.Percent ? PercentScale : 1;

    /// <summary>
    /// Gets the step when none is given: 1 for a property that holds whole numbers, otherwise a hundredth of the range.
    /// </summary>
    public static double GetDefaultStep(Type? propertyType, ValueRange range) =>
        IsWholeNumber(propertyType) ? 1 : (range.Maximum - range.Minimum) / DefaultStepCount;

    /// <summary>
    /// Gets the widget argument error of a range, or null when both ends are finite and the maximum is above the minimum.
    /// </summary>
    public static string? ValidateRange(ValueRange range) =>
        double.IsFinite(range.Minimum) && double.IsFinite(range.Maximum) && range.Maximum > range.Minimum
            ? null
            : $"Maximum must be above Minimum: {Format(range.Minimum)} to {Format(range.Maximum)}";

    /// <summary>
    /// Gets the widget argument error of a step, or null when the step is not set or fits the property type.
    /// </summary>
    public static string? ValidateStep(Type? propertyType, double? step)
    {
        if (step is not { } value)
        {
            return null;
        }

        if (!(value > 0) || double.IsInfinity(value))
        {
            return $"Step must be above 0: {Format(value)}";
        }

        if (IsWholeNumber(propertyType) && Math.Abs(value - Math.Round(value)) > WholeTolerance)
        {
            return $"Step must be a whole number for a whole number property: {Format(value)}";
        }

        return null;
    }

    /// <summary>
    /// Gets the position of the slider for a value: the number times the scale, or the minimum when the value is
    /// missing or no number.
    /// </summary>
    public static double ToSliderValue(object? value, ValueRange range, double scale)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsNaN(number))
            {
                return Math.Round(number * scale, MaximumDecimals);
            }
        }

        return Math.Round(range.Minimum * scale, MaximumDecimals);
    }

    /// <summary>
    /// Gets the value to write for a position of the slider: divided by the scale, rounded to the nearest step from
    /// the minimum and kept inside the range.
    /// </summary>
    public static double ToPropertyValue(double sliderValue, ValueRange range, double step, double scale)
    {
        var value = sliderValue / scale;
        var steps = Math.Round((value - range.Minimum) / step, MidpointRounding.AwayFromZero);
        var rounded = Math.Clamp(range.Minimum + steps * step, range.Minimum, range.Maximum);
        return Math.Round(rounded, GetDecimals(step));
    }

    private static bool IsWholeNumber(Type? type)
    {
        var underlyingType = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        return underlyingType == typeof(byte) || underlyingType == typeof(sbyte) ||
            underlyingType == typeof(short) || underlyingType == typeof(ushort) ||
            underlyingType == typeof(int) || underlyingType == typeof(uint) ||
            underlyingType == typeof(long) || underlyingType == typeof(ulong);
    }

    // A sum of steps keeps the rest of binary fractions (0.1 * 3 is 0.30000000000000004), which rounding to the
    // decimals of the step removes.
    private static int GetDecimals(double step)
    {
        var scaled = step;
        for (var decimals = 0; decimals < MaximumDecimals; decimals++)
        {
            if (Math.Abs(scaled - Math.Round(scaled)) < WholeTolerance * Math.Max(1, Math.Abs(scaled)))
            {
                return decimals;
            }

            scaled *= 10;
        }

        return MaximumDecimals;
    }

    private static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}

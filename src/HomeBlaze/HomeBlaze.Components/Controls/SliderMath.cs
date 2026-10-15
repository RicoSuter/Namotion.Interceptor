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
                return ToSliderScale(number, scale);
            }
        }

        return ToSliderScale(range.Minimum, scale);
    }

    /// <summary>
    /// Gets a number in the unit of the property times the scale, without the rest of binary fractions
    /// (0.29 * 100 is 28.999999999999996).
    /// </summary>
    public static double ToSliderScale(double value, double scale) => Math.Round(value * scale, MaximumDecimals);

    /// <summary>
    /// Gets the value to write for a position of the slider: divided by the scale, rounded to the nearest step from
    /// the minimum and kept inside the range. For a property that holds whole numbers it is then rounded to a whole
    /// number, half away from zero, and kept inside the whole numbers of the range when there are any.
    /// </summary>
    public static double ToPropertyValue(double sliderValue, ValueRange range, double step, double scale, Type? propertyType)
    {
        var value = sliderValue / scale;
        var steps = Math.Round((value - range.Minimum) / step, MidpointRounding.AwayFromZero);
        var decimals = Math.Max(GetDecimals(step), GetDecimals(range.Minimum));
        var rounded = Math.Round(Math.Clamp(range.Minimum + steps * step, range.Minimum, range.Maximum), decimals);

        // `decimals` comes from the step and the minimum, not the maximum, so a value clamped onto a fractional
        // maximum can round up past it; step back onto the largest step from the minimum that still fits, and
        // likewise step up to the minimum when the rounding went the other way.
        if (rounded > range.Maximum)
        {
            var maximumSteps = Math.Floor((range.Maximum - range.Minimum) / step + WholeTolerance);
            rounded = Math.Clamp(Math.Round(range.Minimum + maximumSteps * step, decimals), range.Minimum, range.Maximum);
        }
        else if (rounded < range.Minimum)
        {
            rounded = range.Minimum;
        }

        if (!IsWholeNumber(propertyType))
        {
            return rounded;
        }

        // A range end with a fraction puts the steps between whole numbers, which the conversion to the type would
        // round half to even and so off the step.
        var whole = Math.Round(rounded, MidpointRounding.AwayFromZero);
        var lowest = Math.Ceiling(range.Minimum);
        var highest = Math.Floor(range.Maximum);
        return lowest <= highest ? Math.Clamp(whole, lowest, highest) : whole;
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
    // decimals of the step and the minimum removes.
    private static int GetDecimals(double number)
    {
        var scaled = number;
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

    internal static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}

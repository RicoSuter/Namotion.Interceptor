using System.Globalization;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Decides what a <see cref="Gauge"/> shows for a reading.
/// </summary>
internal static class GaugeDisplay
{
    // Built once, so that a render does not format the style of the ring again.
    private static readonly string SmallRingStyle = CreateRingStyle(GaugeSize.Small);
    private static readonly string MediumRingStyle = CreateRingStyle(GaugeSize.Medium);
    private static readonly string LargeRingStyle = CreateRingStyle(GaugeSize.Large);

    /// <summary>
    /// Gets the state of a gauge: its range by <see cref="ValueRangeResolver"/> and where the value stands in it.
    /// A path that leads nowhere gives an empty ring without an error, as every value widget shows a dash for it.
    /// A range whose maximum is not above its minimum gives the error of <see cref="ValueRangeResolver.Validate"/>.
    /// </summary>
    public static GaugeState GetState(ValueReading reading, double? minimum, double? maximum)
    {
        if (!ValueRangeResolver.TryResolve(reading.Property, minimum, maximum, out var range, out var error))
        {
            return reading.Property is null ? default : new GaugeState(null, null, null, null, error);
        }

        var rangeError = ValueRangeResolver.Validate(range);
        if (rangeError is not null)
        {
            return new GaugeState(null, null, null, null, rangeError);
        }

        var percent = ValueReader.GetProgressPercent(reading.Value, range.Minimum, range.Maximum);
        if (percent is null)
        {
            return new GaugeState(null, null, null, range, null);
        }

        var value = Convert.ToDouble(reading.Value, CultureInfo.InvariantCulture);
        return new GaugeState(value, Math.Clamp(value, range.Minimum, range.Maximum), percent, range, null);
    }

    /// <summary>
    /// Gets the colour of the ring: that of the matched case of the map, otherwise the primary colour.
    /// </summary>
    public static Color GetColor(ValueReading reading) =>
        reading.Color == Color.Default ? Color.Primary : reading.Color;

    /// <summary>
    /// Gets the width and height of the ring in pixels.
    /// </summary>
    public static int GetPixelSize(GaugeSize size) => size switch
    {
        GaugeSize.Small => 72,
        GaugeSize.Large => 160,
        _ => 112,
    };

    /// <summary>
    /// Gets the inline style that gives the ring its width and height.
    /// </summary>
    public static string GetRingStyle(GaugeSize size) => size switch
    {
        GaugeSize.Small => SmallRingStyle,
        GaugeSize.Large => LargeRingStyle,
        _ => MediumRingStyle,
    };

    /// <summary>
    /// Gets the typography of the value in the centre of the ring.
    /// </summary>
    public static Typo GetValueTypo(GaugeSize size) => size switch
    {
        GaugeSize.Small => Typo.body2,
        GaugeSize.Large => Typo.h4,
        _ => Typo.h6,
    };

    /// <summary>
    /// Writes a number for an aria attribute, without the culture of the server. Null stays null, so that the attribute is left out.
    /// </summary>
    public static string? FormatNumber(double? number) =>
        number?.ToString(CultureInfo.InvariantCulture);

    private static string CreateRingStyle(GaugeSize size)
    {
        var pixels = GetPixelSize(size).ToString(CultureInfo.InvariantCulture);
        return $"width: {pixels}px; height: {pixels}px;";
    }
}

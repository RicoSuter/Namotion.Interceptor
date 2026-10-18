using System.Globalization;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Decides what a <see cref="Gauge"/> shows for a reading.
/// </summary>
internal static class GaugeDisplay
{
    // Built once, so that a render does not format the style of the gauge again.
    private static readonly string SmallSizeStyle = CreateSizeStyle(GaugeSize.Small);
    private static readonly string MediumSizeStyle = CreateSizeStyle(GaugeSize.Medium);
    private static readonly string LargeSizeStyle = CreateSizeStyle(GaugeSize.Large);

    /// <summary>
    /// Gets the color of the ring: that of the matched case of the map, otherwise the primary color.
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
    /// Gets the inline style that gives the gauge the diameter of its ring, from which GaugeWidget.razor.css sizes the ring
    /// and its text.
    /// </summary>
    public static string GetSizeStyle(GaugeSize size) => size switch
    {
        GaugeSize.Small => SmallSizeStyle,
        GaugeSize.Large => LargeSizeStyle,
        _ => MediumSizeStyle,
    };

    /// <summary>
    /// Writes a number for an aria attribute, without the culture of the server. Null stays null, so that the attribute is left out.
    /// </summary>
    public static string? FormatNumber(double? number) =>
        number?.ToString(CultureInfo.InvariantCulture);

    private static string CreateSizeStyle(GaugeSize size)
    {
        var pixels = GetPixelSize(size).ToString(CultureInfo.InvariantCulture);
        return $"--gauge-size: {pixels}px;";
    }
}

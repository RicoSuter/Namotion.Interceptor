using MudBlazor;

namespace Namotion.Devices.Nuki.HomeBlaze;

/// <summary>
/// Converts the icon color of a Nuki subject into the color of a widget.
/// </summary>
internal static class NukiWidgetColors
{
    /// <summary>Gets the color with the name <paramref name="iconColor"/>, or <see cref="Color.Default"/> when there is none.</summary>
    public static Color FromIconColor(string? iconColor) =>
        Enum.TryParse<Color>(iconColor, out var color) ? color : Color.Default;
}

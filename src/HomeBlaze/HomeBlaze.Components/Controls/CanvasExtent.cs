using System.Globalization;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Gets the design width of a <see cref="CanvasLayout"/> and the factor it is drawn at.
/// </summary>
internal static class CanvasExtent
{
    /// <summary>
    /// Gets the width the canvas was designed for: the given width, else the right edge of its rightmost node, else 0.
    /// </summary>
    public static int GetDesignWidth(int? width, IReadOnlyList<CanvasNode> nodes)
    {
        if (width is { } pixels)
        {
            return pixels;
        }

        var right = 0;
        foreach (var node in nodes)
        {
            right = Math.Max(right, node.X + node.Width);
        }

        return right;
    }

    /// <summary>Gets the widget argument error of the width, or null when it can be used.</summary>
    public static string? Validate(int? width) =>
        width is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"Width must be above 0: {width}") : null;

    /// <summary>
    /// Gets the factor a canvas is drawn at: the available width over the design width for a viewed canvas that fits the
    /// width, else 1, also while either width is unknown.
    /// </summary>
    public static double GetFactor(CanvasScale scale, bool isEditing, int designWidth, double availableWidth) =>
        scale == CanvasScale.FitWidth && !isEditing && designWidth > 0 && availableWidth > 0
            ? availableWidth / designWidth
            : 1;
}

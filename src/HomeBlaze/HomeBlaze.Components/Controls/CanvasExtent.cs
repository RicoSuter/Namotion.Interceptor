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

    /// <summary>Gets the widget argument error of the width or the least width, or null when both can be used.</summary>
    public static string? Validate(int? width, int? minWidth) =>
        width is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"Width must be above 0: {width}")
        : minWidth is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"MinWidth must be above 0: {minWidth}")
        : null;

    /// <summary>Gets whether a canvas is scaled to its width: a viewed canvas that fits the width and has a design width.</summary>
    public static bool IsScaled(CanvasScale scale, bool isEditing, int designWidth) =>
        scale == CanvasScale.FitWidth && !isEditing && designWidth > 0;

    /// <summary>
    /// Gets the factor a canvas is drawn at: the available width, or the least width when the available one is smaller,
    /// over the design width for a canvas that <see cref="IsScaled"/>, else 1, also while the available width is unknown.
    /// </summary>
    public static double GetFactor(CanvasScale scale, bool isEditing, int designWidth, double availableWidth, int? minWidth) =>
        IsScaled(scale, isEditing, designWidth) && availableWidth > 0
            ? Math.Max(availableWidth, minWidth ?? 0) / designWidth
            : 1;
}

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
    /// Gets the CSS width a canvas that <see cref="IsScaled"/> is drawn at: the width of its frame, which must be an
    /// inline-size container, but no less than the least width.
    /// </summary>
    public static string GetDrawnWidth(int? minWidth) =>
        minWidth is { } pixels
            ? string.Create(CultureInfo.InvariantCulture, $"max(100cqw, {pixels}px)")
            : "100cqw";

    /// <summary>
    /// Gets the CSS number a canvas that <see cref="IsScaled"/> is drawn at: its <see cref="GetDrawnWidth"/> over its
    /// design width.
    /// </summary>
    // CSS divides a length by a length only through the tangent of the angle between them.
    public static string GetFactor(int designWidth, int? minWidth) =>
        string.Create(CultureInfo.InvariantCulture, $"tan(atan2({GetDrawnWidth(minWidth)}, {designWidth}px))");
}

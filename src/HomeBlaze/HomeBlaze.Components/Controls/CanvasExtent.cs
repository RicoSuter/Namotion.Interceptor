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

    /// <summary>
    /// Gets the widget argument error of the width, the least width or the largest width, or null when all can be used.
    /// </summary>
    public static string? Validate(int? width, int? minWidth, int? maxWidth) =>
        width is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"Width must be above 0: {width}")
        : minWidth is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"MinWidth must be above 0: {minWidth}")
        : maxWidth is <= 0 ? string.Create(CultureInfo.InvariantCulture, $"MaxWidth must be above 0: {maxWidth}")
        : maxWidth < minWidth ? string.Create(CultureInfo.InvariantCulture, $"MaxWidth must not be below MinWidth: {maxWidth}")
        : null;

    /// <summary>Gets whether a canvas is scaled to its width: a viewed canvas that fits the width and has a design width.</summary>
    public static bool IsScaled(CanvasScale scale, bool isEditing, int designWidth) =>
        scale == CanvasScale.FitWidth && !isEditing && designWidth > 0;

    /// <summary>
    /// Gets the CSS width a canvas that <see cref="IsScaled"/> is drawn at: the width of its frame, which must be an
    /// inline-size container, but no more than the largest width and no less than the least width, which wins.
    /// </summary>
    public static string GetDrawnWidth(int? minWidth, int? maxWidth) => (minWidth, maxWidth) switch
    {
        ({ } least, { } largest) => string.Create(CultureInfo.InvariantCulture, $"clamp({least}px, 100cqw, {largest}px)"),
        ({ } least, null) => string.Create(CultureInfo.InvariantCulture, $"max(100cqw, {least}px)"),
        (null, { } largest) => string.Create(CultureInfo.InvariantCulture, $"min(100cqw, {largest}px)"),
        _ => "100cqw"
    };

    /// <summary>
    /// The custom property that holds the <see cref="GetAngle"/> of a canvas, for <see cref="Factor"/> on the element that
    /// sets it and on its descendants.
    /// </summary>
    // Must match the @property registration in CanvasLayoutWidget.razor.css.
    public const string AngleProperty = "--canvas-scale-angle";

    /// <summary>
    /// Gets the CSS angle whose tangent is the factor a canvas that <see cref="IsScaled"/> is drawn at: <c>atan2</c> of its
    /// <see cref="GetDrawnWidth"/> and its design width.
    /// </summary>
    // Not calc(length / length): typed division is not supported in every target browser.
    public static string GetAngle(string drawnWidth, int designWidth) =>
        string.Create(CultureInfo.InvariantCulture, $"atan2({drawnWidth}, {designWidth}px)");

    /// <summary>
    /// The CSS number a canvas that <see cref="IsScaled"/> is drawn at, the tangent of the <see cref="AngleProperty"/> on the
    /// element or an ancestor: its <see cref="GetDrawnWidth"/> over its design width.
    /// </summary>
    // Do not inline tan(atan2(...)): WebKit computes it wrong when atan2 depends on the layout (cqw), and right through the
    // registered angle property.
    public const string Factor = "tan(var(" + AngleProperty + "))";
}

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
    /// The custom property that holds the <see cref="GetAngle"/> of a canvas, which the canvas widget sets on an element in
    /// the frame, so that the element and its descendants can use the <see cref="Factor"/>.
    /// </summary>
    // Registered as an inherited angle in CanvasLayoutWidget.razor.css, which must use the same name.
    public const string AngleProperty = "--canvas-scale-angle";

    /// <summary>
    /// Gets the CSS angle whose tangent is the factor a canvas that <see cref="IsScaled"/> is drawn at: the angle between
    /// its <see cref="GetDrawnWidth"/> and its design width.
    /// </summary>
    // CSS divides a length by a length only through the tangent of the angle between them.
    public static string GetAngle(int designWidth, int? minWidth, int? maxWidth) =>
        string.Create(CultureInfo.InvariantCulture, $"atan2({GetDrawnWidth(minWidth, maxWidth)}, {designWidth}px)");

    /// <summary>
    /// The CSS number a canvas that <see cref="IsScaled"/> is drawn at, from the <see cref="AngleProperty"/> set on the
    /// element or an ancestor in the frame: its <see cref="GetDrawnWidth"/> over its design width.
    /// </summary>
    // Not tan(atan2(...)) directly: WebKit takes an atan2 that depends on the layout, as one of cqw does, for degrees and
    // its tangent for radians. It computes the registered angle property right.
    public const string Factor = "tan(var(" + AngleProperty + "))";
}

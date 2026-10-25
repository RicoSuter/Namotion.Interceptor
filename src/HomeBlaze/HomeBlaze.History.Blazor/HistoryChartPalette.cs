using System.Globalization;
using System.Text;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// The colors of history charts and the background that draws a state band.
/// </summary>
internal static class HistoryChartPalette
{
    /// <summary>The hatch of a span whose value is unknown: no store vouches for it, or nothing was recorded in it yet.</summary>
    public const string UnknownFill =
        "repeating-linear-gradient(45deg, #BDBDBD, #BDBDBD 4px, #E0E0E0 4px, #E0E0E0 8px)";

    /// <summary>The legend text of a span whose value is unknown.</summary>
    public const string UnknownLabel = "No data";

    /// <summary>
    /// The fill of a span whose value was recorded as absent. It must not read as missing history: the
    /// value there is known to be null.
    /// </summary>
    public const string NoValueFill = "#455A64";

    /// <summary>The legend text of a span whose value was recorded as absent.</summary>
    public const string NoValueLabel = "(no value)";

    /// <summary>The line colors in the order the lines of a chart take them; the first is that of the history dialog.</summary>
    public static readonly IReadOnlyList<string> LineColors =
        ["#2979FF", "#00B8A9", "#F6A609", "#8E44AD", "#E4572E", "#3D5A80", "#C2185B"];

    /// <summary>Gets the color of the line at <paramref name="index"/>, repeating after the last color.</summary>
    /// <param name="index">The zero-based position of the line in its chart.</param>
    /// <returns>The CSS color of the line.</returns>
    public static string GetLineColor(int index) => LineColors[index % LineColors.Count];

    /// <summary>Gets the text a segment stands for in a legend or a table.</summary>
    /// <param name="segment">The segment to describe.</param>
    /// <returns>The state label, <see cref="UnknownLabel"/> or <see cref="NoValueLabel"/>.</returns>
    public static string DescribeSegment(PropertyHistoryChartModel.StateSegment segment) =>
        segment.IsUnknown ? UnknownLabel : segment.Label ?? NoValueLabel;

    /// <summary>
    /// Builds the CSS background of a band as one gradient with hard color stops rather than one element
    /// per run. A chattering property yields hundreds of sub-pixel runs, and as elements those cannot
    /// shrink below their minimum width, so the long runs lose their proportion and the newest runs are
    /// clipped off the end.
    /// </summary>
    /// <param name="segments">The runs of the band in time order, filling the window.</param>
    /// <param name="from">The start of the window.</param>
    /// <param name="to">The end of the window.</param>
    /// <param name="palette">The state palette of the chart; colors the labels on first use.</param>
    /// <returns>The CSS background, or <see cref="UnknownFill"/> for an empty window.</returns>
    public static string BuildBandBackground(
        IReadOnlyList<PropertyHistoryChartModel.StateSegment> segments,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryStatePalette palette)
    {
        var window = (to - from).Ticks;
        if (window <= 0)
        {
            return UnknownFill;
        }

        var builder = new StringBuilder(64 + segments.Count * 48);
        builder.Append("linear-gradient(to right, ");

        var offset = 0d;
        var hasUnknown = false;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            hasUnknown |= segment.IsUnknown;

            // An unknown run punches a transparent hole into the color layer, through which the hatch shows.
            var fill = segment.IsUnknown
                ? "transparent"
                : segment.Label is { } label ? palette.GetColor(label) : NoValueFill;
            var next = offset + segment.Duration.Ticks * 100d / window;
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(CultureInfo.InvariantCulture, $"{fill} {offset:0.####}%, {fill} {next:0.####}%");
            offset = next;
        }

        builder.Append(')');

        // The hatch is itself a gradient, so it cannot be a stop of a linear-gradient: it is layered below.
        if (hasUnknown)
        {
            builder.Append(", ").Append(UnknownFill);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the legend of a band: the states it shows in the order the palette colored them, then the unknown
    /// and the absent value when it shows them.
    /// </summary>
    /// <param name="segments">The runs of the band.</param>
    /// <param name="palette">The state palette that colored the band.</param>
    /// <returns>The legend entries in display order.</returns>
    public static IReadOnlyList<HistoryChartLegendEntry> BuildBandLegend(
        IReadOnlyList<PropertyHistoryChartModel.StateSegment> segments, HistoryStatePalette palette)
    {
        var hasUnknown = false;
        var hasNoValue = false;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            hasUnknown |= segment.IsUnknown;
            hasNoValue |= !segment.IsUnknown && segment.Label is null;
        }

        var assigned = palette.Assigned;
        var legend = new List<HistoryChartLegendEntry>(assigned.Count + 2);
        for (var index = 0; index < assigned.Count; index++)
        {
            if (Contains(segments, assigned[index].Label))
            {
                legend.Add(assigned[index]);
            }
        }

        if (hasUnknown)
        {
            legend.Add(new HistoryChartLegendEntry(UnknownLabel, UnknownFill));
        }

        if (hasNoValue)
        {
            legend.Add(new HistoryChartLegendEntry(NoValueLabel, NoValueFill));
        }

        return legend;
    }

    private static bool Contains(IReadOnlyList<PropertyHistoryChartModel.StateSegment> segments, string label)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!segment.IsUnknown && segment.Label == label)
            {
                return true;
            }
        }

        return false;
    }
}

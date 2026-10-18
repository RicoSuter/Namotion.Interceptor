using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// What a history chart draws over one time window: lines for numbers and bands for states.
/// The chart view draws again only when it gets another instance.
/// </summary>
/// <param name="From">The start of the window.</param>
/// <param name="To">The end of the window.</param>
/// <param name="Bucket">The bucket of the query, or null for raw samples.</param>
/// <param name="Lines">The number series in legend order.</param>
/// <param name="Bands">The state series in legend order.</param>
public sealed record HistoryChartData(
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket,
    IReadOnlyList<HistoryChartLine> Lines,
    IReadOnlyList<HistoryChartBand> Bands)
{
    /// <summary>Gets whether there is nothing to draw: no band and no line with a point.</summary>
    public bool IsEmpty
    {
        get
        {
            if (Bands.Count > 0)
            {
                return false;
            }

            for (var index = 0; index < Lines.Count; index++)
            {
                if (Lines[index].Runs.Count > 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>A number series: its gap-free runs of points, each drawn as one line in <paramref name="Color"/>.</summary>
/// <param name="Label">The name of the series.</param>
/// <param name="Color">The CSS color of the line, which the chart draws without a check: pass only a plain color such as "#E4572E", never a value from an untrusted source.</param>
/// <param name="Runs">The gap-free runs of points in time order.</param>
/// <param name="IsPercent">Whether the points are fractions that are drawn in percent, see <see cref="HistoryChartAxis.ToPercent"/>.</param>
public sealed record HistoryChartLine(
    string Label, string Color, IReadOnlyList<IReadOnlyList<HistoryPoint>> Runs, bool IsPercent = false);

/// <summary>A state series: its constant-value runs, the CSS background that draws them and the legend of its states.</summary>
/// <param name="Label">The name of the series.</param>
/// <param name="Segments">The constant-value runs in time order, filling the window.</param>
/// <param name="Background">The CSS background of the band, see <see cref="HistoryChartPalette.BuildBandBackground"/>.</param>
/// <param name="Legend">The states the band shows with their fills, see <see cref="HistoryChartPalette.BuildBandLegend"/>.</param>
public sealed record HistoryChartBand(
    string Label,
    IReadOnlyList<PropertyHistoryChartModel.StateSegment> Segments,
    string Background,
    IReadOnlyList<HistoryChartLegendEntry> Legend);

/// <summary>A state of the legend and its CSS fill.</summary>
/// <param name="Label">The text of the state.</param>
/// <param name="Fill">The CSS fill of the state, a color or a gradient.</param>
public readonly record struct HistoryChartLegendEntry(string Label, string Fill);

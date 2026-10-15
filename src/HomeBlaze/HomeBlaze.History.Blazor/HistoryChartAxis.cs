namespace HomeBlaze.History.Blazor;

/// <summary>
/// The value axis of the lines of a history chart.
/// </summary>
/// <param name="Step">The whole number between two value labels.</param>
/// <param name="IncludesZero">Whether the axis reaches to zero.</param>
/// <param name="SuggestedMaximum">The value the axis reaches at least, or null.</param>
public readonly record struct HistoryChartValueAxis(int Step, bool IncludesZero, double? SuggestedMaximum);

/// <summary>
/// Spaces the labels of the axes of a history chart by the size it is drawn at.
/// </summary>
public static class HistoryChartAxis
{
    /// <summary>The height one value label takes, in pixels.</summary>
    public const double PixelsPerValueLabel = 55;

    /// <summary>The width one time label takes, in pixels.</summary>
    public const double PixelsPerTimeLabel = 90;

    /// <summary>The most time labels a chart shows, however wide it is.</summary>
    public const int MaximumTimeLabelCount = 9;

    private const int LargestStep = 1_000_000_000;

    /// <summary>
    /// Gets how many value labels fit into lines of the height: one per <see cref="PixelsPerValueLabel"/>, at least two.
    /// </summary>
    /// <param name="height">The height of the lines in pixels.</param>
    public static int GetValueLabelCount(double height) =>
        double.IsFinite(height) && height >= 2 * PixelsPerValueLabel ? (int)(height / PixelsPerValueLabel) : 2;

    /// <summary>
    /// Gets the value axis of lines whose values reach from <paramref name="minimum"/> to <paramref name="maximum"/>:
    /// the smallest step of 1, 2, 2.5 or 5 times a power of ten that needs no more labels than the height holds.
    /// A constant value gets an axis that reaches to zero, so its line is not drawn on the edge of a one-label axis.
    /// </summary>
    /// <param name="minimum">The lowest value of the lines.</param>
    /// <param name="maximum">The highest value of the lines.</param>
    /// <param name="height">The height of the lines in pixels.</param>
    public static HistoryChartValueAxis GetValueAxis(double minimum, double maximum, double height)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            return new HistoryChartValueAxis(1, false, null);
        }

        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        var includesZero = false;
        double? suggestedMaximum = null;
        if (minimum == maximum)
        {
            includesZero = true;
            minimum = Math.Min(minimum, 0);
            maximum = Math.Max(maximum, 0);
            if (minimum == maximum)
            {
                maximum = 1;
                suggestedMaximum = 1;
            }
        }

        var labelCount = GetValueLabelCount(height);
        for (var magnitude = 1L; magnitude <= LargestStep; magnitude *= 10)
        {
            // 2.5 times the magnitude only from 25 on, because the chart takes a whole number as its step.
            var step = Fits(magnitude) ?? Fits(2 * magnitude) ?? (magnitude >= 10 ? Fits(magnitude / 2 * 5) : null) ?? Fits(5 * magnitude);
            if (step is not null)
            {
                return new HistoryChartValueAxis(step.Value, includesZero, suggestedMaximum);
            }
        }

        // The chart doubles the step until its labels fit, so a range beyond the largest step still gets an axis.
        return new HistoryChartValueAxis(LargestStep, includesZero, suggestedMaximum);

        int? Fits(long step) =>
            step <= int.MaxValue && Math.Ceiling(maximum / step) - Math.Floor(minimum / step) + 1 <= labelCount ? (int)step : null;
    }

    /// <summary>
    /// Gets the format of the time labels, which tells every label of the range apart: seconds for up to ten
    /// minutes, the time of day for less than a day, the weekday with the time for up to two days, and the date
    /// beyond that.
    /// </summary>
    /// <param name="range">The time span the lines show.</param>
    public static string GetTimeLabelFormat(TimeSpan range) =>
        range <= TimeSpan.FromMinutes(10) ? "HH:mm:ss" :
        range < TimeSpan.FromDays(1) ? "HH:mm" :
        range <= TimeSpan.FromDays(2) ? "ddd HH:mm" :
        "MM-dd";

    /// <summary>
    /// Gets the time between two time labels: the range split into as many parts as the width holds labels, at
    /// most <see cref="MaximumTimeLabelCount"/> labels and at least two, never shorter than the bucket, and whole
    /// days when the labels show dates.
    /// </summary>
    /// <param name="range">The time span the lines show.</param>
    /// <param name="bucket">The interval one point stands for, or null for raw samples.</param>
    /// <param name="width">The width of the lines in pixels.</param>
    public static TimeSpan GetTimeLabelSpacing(TimeSpan range, TimeSpan? bucket, double width)
    {
        var labelCount = double.IsFinite(width) ? (int)Math.Clamp(width / PixelsPerTimeLabel, 2, MaximumTimeLabelCount) : 2;
        var spacing = range > TimeSpan.Zero ? TimeSpan.FromTicks(range.Ticks / (labelCount - 1)) : TimeSpan.FromMinutes(1);
        if (bucket is { } size && size > spacing)
        {
            spacing = size;
        }

        return range > TimeSpan.FromDays(2) ? TimeSpan.FromDays(Math.Ceiling(spacing.TotalDays)) : spacing;
    }
}

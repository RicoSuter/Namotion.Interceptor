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

    /// <summary>The round steps between two time labels, from the shortest to the longest.</summary>
    public static readonly IReadOnlyList<TimeSpan> TimeLabelSteps =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
        TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(3), TimeSpan.FromDays(7),
        TimeSpan.FromDays(14), TimeSpan.FromDays(30)
    ];

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
    /// Gets how many time labels fit into lines of the width: one per <see cref="PixelsPerTimeLabel"/>, at least two
    /// and at most <see cref="MaximumTimeLabelCount"/>.
    /// </summary>
    /// <param name="width">The width of the lines in pixels.</param>
    public static int GetTimeLabelCount(double width) =>
        double.IsFinite(width) ? (int)Math.Clamp(width / PixelsPerTimeLabel, 2, MaximumTimeLabelCount) : 2;

    /// <summary>
    /// Gets the format of the time labels, which tells every label apart: seconds for a spacing under a minute, the
    /// date for a spacing of whole days, the time of day for a range under a day, and the weekday with the time
    /// otherwise.
    /// </summary>
    /// <param name="range">The time span the lines show.</param>
    /// <param name="spacing">The time between two time labels.</param>
    public static string GetTimeLabelFormat(TimeSpan range, TimeSpan spacing) =>
        spacing < TimeSpan.FromMinutes(1) ? "HH:mm:ss" :
        spacing >= TimeSpan.FromDays(1) ? "MM-dd" :
        range < TimeSpan.FromDays(1) ? "HH:mm" :
        "ddd HH:mm";

    /// <summary>
    /// Gets the time between two time labels: the smallest round step of <see cref="TimeLabelSteps"/> that splits the
    /// range into no more parts than the width holds labels, never shorter than the bucket. When that step leaves
    /// less than two parts, the largest step that still splits the range in two is taken, so a narrow chart keeps more
    /// than one label. With the label rounding of the chart the labels then fall on round times.
    /// </summary>
    /// <param name="range">The time span the lines show.</param>
    /// <param name="bucket">The interval one point stands for, or null for raw samples.</param>
    /// <param name="width">The width of the lines in pixels.</param>
    public static TimeSpan GetTimeLabelSpacing(TimeSpan range, TimeSpan? bucket, double width)
    {
        var minimum = bucket ?? TimeSpan.Zero;
        var spacing = range > TimeSpan.Zero ? range / (GetTimeLabelCount(width) - 1) : TimeSpan.Zero;
        TimeSpan? halving = null;
        foreach (var step in TimeLabelSteps)
        {
            if (step < minimum)
            {
                continue;
            }

            if (step >= spacing)
            {
                return range >= 2 * step ? step : halving ?? step;
            }

            if (range >= 2 * step)
            {
                halving = step;
            }
        }

        return halving ?? TimeLabelSteps[^1];
    }
}

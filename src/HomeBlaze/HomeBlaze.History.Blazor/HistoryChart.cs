using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Shows the recorded history of numbers as lines and of states as bands in one chart.
/// </summary>
[Category("Dashboard")]
[Description("Shows the recorded history of numbers as lines and of states as bands")]
[DisplayName("History chart")]
[SubjectIcon("ShowChart")]
[PageLayout]
[InterceptorSubject]
public partial class HistoryChart : IConfigurable, ITitleProvider
{
    public HistoryChart()
    {
        Range = "24h";
        Bucket = "Auto";
        Height = 300;
        RefreshInterval = HistoryRefreshThrottle.MinimumInterval;
        Series = [];
    }

    /// <summary>
    /// The time span that ends now, a short name of <see cref="PropertyHistoryChartModel.RangePresets"/>: 1h, 6h, 24h, 7d or 30d.
    /// </summary>
    [Configuration]
    public partial string Range { get; set; }

    /// <summary>
    /// The interval one point of a line stands for, a short name of <see cref="PropertyHistoryChartModel.Periods"/>:
    /// Auto, Raw or a fixed size such as 5m. A fixed size must divide the range into at most
    /// <see cref="PropertyHistoryChartModel.MaxPoints"/> intervals.
    /// </summary>
    [Configuration]
    public partial string Bucket { get; set; }

    /// <summary>
    /// How the values in a bucket become one point of a line, a name of <see cref="HomeBlaze.History.Abstractions.HistoryAggregations"/>
    /// such as Maximum. Null or empty for the automatic choice: the first aggregation the property and the stores offer.
    /// A band and a raw bucket ignore it.
    /// </summary>
    [Configuration]
    public partial string? Aggregation { get; set; }

    /// <summary>
    /// The height of the lines in pixels, above 0. On a narrow screen the lines get lower to keep their proportions.
    /// </summary>
    [Configuration]
    public partial int Height { get; set; }

    /// <summary>
    /// How often the chart queries again at most while its values change. Less than a minute counts as a minute, a bucket
    /// longer than the interval lengthens it to the bucket, and more than a day is an error.
    /// </summary>
    [Configuration]
    public partial TimeSpan RefreshInterval { get; set; }

    /// <summary>
    /// Whether the chart leaves out the labels of both axes, its grid lines and the tooltips of its lines, so its lines
    /// fill the whole width. For a small chart that opens the history dialog for the detail.
    /// </summary>
    [Configuration]
    public partial bool Compact { get; set; }

    /// <summary>
    /// The series of the chart. On a page one <c>Series</c> block each, in a configuration file a list.
    /// </summary>
    [Configuration, Slot("Series")]
    public partial HistoryChartSeries[] Series { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

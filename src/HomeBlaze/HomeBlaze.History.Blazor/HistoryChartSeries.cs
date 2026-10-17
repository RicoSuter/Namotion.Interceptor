using System.Numerics;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// One series of a <see cref="HistoryChart"/>: a number is a line, a boolean or an enum is a state band.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class HistoryChartSeries : IConfigurable
{
    /// <summary>
    /// Path of the property whose history the series shows.
    /// </summary>
    [Configuration]
    [SubjectPath(ValueTypes = [typeof(INumber<>), typeof(bool), typeof(Enum)])]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label in the legend. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// The CSS color of a line, for example "#E4572E", a name such as "teal", or rgb(), rgba(), hsl() or hsla() with
    /// plain numbers. Defaults to the next color of the chart, which is also used for a value that is not such a plain
    /// CSS color. A state band has one color per state and ignores it.
    /// </summary>
    [Configuration]
    public partial string? Color { get; set; }

    /// <summary>
    /// The aggregation of a line in place of the one of the chart, a name of
    /// <see cref="HomeBlaze.History.Abstractions.HistoryAggregations"/>. Null or empty for the one of the chart.
    /// A band ignores it.
    /// </summary>
    [Configuration]
    public partial string? Aggregation { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

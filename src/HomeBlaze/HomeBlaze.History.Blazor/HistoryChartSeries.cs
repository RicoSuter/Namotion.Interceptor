using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// One series of a <see cref="HistoryChart"/>: a number is a line, a boolean or an enum is a state band.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class HistoryChartSeries : IConfigurable
{
    /// <summary>
    /// Path of the property whose history the series shows.
    /// </summary>
    [Configuration]
    [SubjectPath(Kind = SubjectPathKinds.Number | SubjectPathKinds.Boolean | SubjectPathKinds.Enum)]
    public partial string? Path { get; set; }

    /// <summary>
    /// The label in the legend. Defaults to the title the property declares, then to its name.
    /// </summary>
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>
    /// The CSS colour of a line, for example "#E4572E". Defaults to the next colour of the chart.
    /// A state band has one colour per state and ignores it.
    /// </summary>
    [Configuration]
    public partial string? Color { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

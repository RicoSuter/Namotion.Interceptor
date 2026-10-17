using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Draws the lines of a history chart in a MudChart that fits its box. MudChart builds its whole drawing again
/// whenever its parameters are set, so it is rendered again only for other series or options, and the chart view
/// can render its legends on every change of a value.
/// </summary>
internal sealed class HistoryChartPlot : ComponentBase
{
    private List<ChartSeries<double>>? _renderedSeries;
    private TimeSeriesChartOptions? _renderedOptions;

    /// <summary>The series to draw; a new instance draws again.</summary>
    [Parameter, EditorRequired]
    public List<ChartSeries<double>>? ChartSeries { get; set; }

    /// <summary>The options of the chart; a new instance draws again.</summary>
    [Parameter, EditorRequired]
    public TimeSeriesChartOptions? ChartOptions { get; set; }

    protected override bool ShouldRender() =>
        !ReferenceEquals(ChartSeries, _renderedSeries) || !ReferenceEquals(ChartOptions, _renderedOptions);

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _renderedSeries = ChartSeries;
        _renderedOptions = ChartOptions;

        // MatchBoundsToSize sizes the SVG viewBox to the rendered box, so labels and strokes keep their own size
        // instead of being scaled by a fixed 700x350 viewBox.
        builder.OpenComponent<MudChart<double>>(0);
        builder.AddComponentParameter(1, nameof(MudChart<double>.ChartType), ChartType.Timeseries);
        builder.AddComponentParameter(2, nameof(MudChart<double>.ChartSeries), ChartSeries);
        builder.AddComponentParameter(3, nameof(MudChart<double>.ChartOptions), ChartOptions);
        builder.AddComponentParameter(4, nameof(MudChart<double>.MatchBoundsToSize), true);
        builder.AddComponentParameter(5, nameof(MudChart<double>.Width), "100%");
        builder.AddComponentParameter(6, nameof(MudChart<double>.Height), "100%");
        builder.CloseComponent();
    }
}

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Charts;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Draws the lines or the bars of a history chart in a MudChart that fits its box. MudChart builds its whole drawing
/// again whenever its parameters are set, so it is rendered again only for other series or options, and the chart view
/// can render its legends on every change of a value.
/// </summary>
internal sealed class HistoryChartPlot : ComponentBase
{
    private readonly EventCallback<int> _selectedIndexChanged;
    private List<ChartSeries<double>>? _renderedSeries;
    private IChartOptions? _renderedOptions;

    // MudChart reports a click on a line or a bar through SelectedIndexChanged, but only when the index differs from the
    // one it holds. So after each click the selection is set to an index of no series, alternating between two, which
    // MudChart takes as a new value, and a second click on the same series is reported too.
    private int _selectedIndex = -1;
    private int _renderedSelectedIndex = -1;

    public HistoryChartPlot()
    {
        _selectedIndexChanged = EventCallback.Factory.Create<int>(this, OnSelectedIndexChanged);
    }

    /// <summary>The series to draw; a new instance draws again.</summary>
    [Parameter, EditorRequired]
    public List<ChartSeries<double>>? ChartSeries { get; set; }

    /// <summary>
    /// The options of the chart; a new instance draws again. <see cref="BarChartOptions"/> draw bars, one group per
    /// label of <see cref="ChartLabels"/>, and <see cref="TimeSeriesChartOptions"/> draw lines.
    /// </summary>
    [Parameter, EditorRequired]
    public IChartOptions? ChartOptions { get; set; }

    /// <summary>The label of each group of bars, set together with <see cref="ChartOptions"/>. Lines ignore it.</summary>
    [Parameter]
    public string[] ChartLabels { get; set; } = [];

    /// <summary>
    /// Called with the index in <see cref="ChartSeries"/> of the series whose line or bar was clicked, before the click
    /// reaches the elements around the chart.
    /// </summary>
    [Parameter]
    public EventCallback<int> SeriesClicked { get; set; }

    protected override bool ShouldRender() =>
        !ReferenceEquals(ChartSeries, _renderedSeries) ||
        !ReferenceEquals(ChartOptions, _renderedOptions) ||
        _selectedIndex != _renderedSelectedIndex;

    private Task OnSelectedIndexChanged(int index)
    {
        _selectedIndex = _selectedIndex == -1 ? -2 : -1;
        return index >= 0 ? SeriesClicked.InvokeAsync(index) : Task.CompletedTask;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _renderedSeries = ChartSeries;
        _renderedOptions = ChartOptions;
        _renderedSelectedIndex = _selectedIndex;

        // MatchBoundsToSize sizes the SVG viewBox to the rendered box, so labels and strokes keep their own size
        // instead of being scaled by a fixed 700x350 viewBox.
        builder.OpenComponent<MudChart<double>>(0);
        builder.AddComponentParameter(1, nameof(MudChart<double>.ChartType), ChartOptions is BarChartOptions ? ChartType.Bar : ChartType.Timeseries);
        builder.AddComponentParameter(2, nameof(MudChart<double>.ChartSeries), ChartSeries);
        builder.AddComponentParameter(3, nameof(MudChart<double>.ChartOptions), ChartOptions);
        builder.AddComponentParameter(4, nameof(MudChart<double>.MatchBoundsToSize), true);
        builder.AddComponentParameter(5, nameof(MudChart<double>.Width), "100%");
        builder.AddComponentParameter(6, nameof(MudChart<double>.Height), "100%");
        builder.AddComponentParameter(7, nameof(MudChart<double>.ChartLabels), ChartLabels);
        builder.AddComponentParameter(8, nameof(MudChart<double>.SelectedIndex), _selectedIndex);
        builder.AddComponentParameter(9, nameof(MudChart<double>.SelectedIndexChanged), _selectedIndexChanged);
        builder.CloseComponent();
    }
}

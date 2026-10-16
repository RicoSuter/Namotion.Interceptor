namespace HomeBlaze.History.Blazor;

/// <summary>
/// The selections the history dialog opens with. A null member keeps the default of the dialog.
/// </summary>
/// <param name="Range">The time span that ends now: the preset of <see cref="PropertyHistoryChartModel.RangePresets"/> with this span, otherwise a custom range.</param>
/// <param name="Period">The period, one of <see cref="PropertyHistoryChartModel.Periods"/>.</param>
/// <param name="Aggregation">The aggregation, selected when the dialog offers it for the property.</param>
public sealed record PropertyHistoryDialogSettings(
    TimeSpan? Range = null,
    PropertyHistoryChartModel.ChartPeriod? Period = null,
    string? Aggregation = null);

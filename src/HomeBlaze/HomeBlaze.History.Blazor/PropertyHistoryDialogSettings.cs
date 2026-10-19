namespace HomeBlaze.History.Blazor;

/// <summary>
/// The selections the history dialog opens with. A null member keeps the default of the dialog.
/// </summary>
/// <param name="Range">The time span that ends now, one of the presets of the dialog: 1 hour, 6 hours, 24 hours, 7 days or 30 days. Another span selects the shortest preset at least as long, or 30 days for a longer span.</param>
/// <param name="Period">The period, one of <see cref="PropertyHistoryChartModel.Periods"/>.</param>
/// <param name="Aggregation">The aggregation, selected when the dialog offers it for the property.</param>
public sealed record PropertyHistoryDialogSettings(
    TimeSpan? Range = null,
    PropertyHistoryChartModel.ChartPeriod? Period = null,
    string? Aggregation = null);

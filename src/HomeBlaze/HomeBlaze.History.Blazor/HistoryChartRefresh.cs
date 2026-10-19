namespace HomeBlaze.History.Blazor;

/// <summary>
/// Decides when a history chart queries again, through a <see cref="HistoryRefreshThrottle"/>: once after a first query
/// of its series, which can run before the stores hold their latest values, and then at the end of a window after a
/// value of its series changed. Only <see cref="Dispose"/> may be called from another thread than the other members.
/// </summary>
internal sealed class HistoryChartRefresh : IDisposable
{
    private readonly HistoryRefreshThrottle _throttle;
    private IReadOnlyList<HistoryChartSeriesInfo> _series = [];
    private object?[] _values = [];

    /// <param name="timeProvider">The clock of the windows.</param>
    /// <param name="callback">Runs on a timer thread at the end of a window, see <see cref="HistoryRefreshThrottle"/>.</param>
    public HistoryChartRefresh(TimeProvider timeProvider, Action callback)
    {
        _throttle = new HistoryRefreshThrottle(timeProvider, callback);
    }

    /// <summary>Takes the series of the chart and their current values, without starting a window.</summary>
    public void SetSeries(IReadOnlyList<HistoryChartSeriesInfo> series)
    {
        _series = series;
        _values = new object?[series.Count];
        HistoryChartSeriesResolver.UpdateValues(series, _values);
    }

    /// <summary>
    /// Starts a window after a first query of the series, so they are queried once more one full window later. It
    /// replaces a running window, which can be shorter or end too soon after that query.
    /// </summary>
    /// <param name="window">The window of the chart, see <see cref="HistoryRefreshThrottle.GetWindow"/>.</param>
    /// <returns>Whether this call started a window, false only after <see cref="Dispose"/>.</returns>
    public bool OnFirstQueryCompleted(TimeSpan window) => _throttle.Restart(window);

    /// <summary>
    /// Starts a window when a value of the series changed since the last call that ran while no window was running,
    /// or since <see cref="SetSeries"/>. A change seen while a window runs is kept for the first call after it ended.
    /// </summary>
    /// <param name="window">The window of the chart, see <see cref="HistoryRefreshThrottle.GetWindow"/>.</param>
    /// <returns>Whether this call started a window.</returns>
    public bool OnRendered(TimeSpan window) =>
        // The query at the end of a window can miss a change that a store has not flushed yet, so the values are not
        // taken while it runs: the render after that query sees the change and starts one more window.
        !_throttle.IsArmed && HistoryChartSeriesResolver.UpdateValues(_series, _values) && _throttle.Arm(window);

    public void Dispose() => _throttle.Dispose();
}

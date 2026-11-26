namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The step rule of the <see cref="HistoryAggregations.Delta"/> aggregation, shared by every store.
/// </summary>
public static class HistoryDelta
{
    /// <summary>
    /// Returns the increase from <paramref name="previous"/> to <paramref name="current"/>: the difference when the counter
    /// grew or held, the whole <paramref name="current"/> when it dropped below half of <paramref name="previous"/> (a restart
    /// from zero), and 0 for a smaller drop (a step back).
    /// </summary>
    public static double Step(double previous, double current) =>
        current >= previous ? current - previous :
        current < previous / 2 ? current :
        0;
}

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The step rule of the <see cref="HistoryAggregations.Delta"/> aggregation, shared by every store.
/// </summary>
public static class HistoryDelta
{
    /// <summary>
    /// Returns the increase from <paramref name="previous"/> to <paramref name="current"/>. A drop is read as a
    /// counter restart from zero, so the whole <paramref name="current"/> value counts.
    /// </summary>
    public static double Step(double previous, double current) =>
        current >= previous ? current - previous : current;
}

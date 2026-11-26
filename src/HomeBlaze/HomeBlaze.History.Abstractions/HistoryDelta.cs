namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The step rule of the <see cref="HistoryAggregations.Delta"/> aggregation, shared by every store.
/// </summary>
public static class HistoryDelta
{
    /// <summary>
    /// Returns the increase from <paramref name="previous"/> to <paramref name="current"/>. A drop below half of
    /// <paramref name="previous"/> is read as a counter restart from zero, so the whole <paramref name="current"/> value
    /// counts. A smaller drop is read as a step back of the counter and counts as 0; the next step starts from
    /// <paramref name="current"/>.
    /// </summary>
    public static double Step(double previous, double current) =>
        current >= previous ? current - previous :
        current < previous / 2 ? current :
        0;
}

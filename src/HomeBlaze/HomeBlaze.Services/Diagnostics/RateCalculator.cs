namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Computes rates and fractions from two cumulative readings. Every method returns null when no valid value
/// can be computed, so callers keep their previous value.
/// </summary>
internal static class RateCalculator
{
    /// <summary>
    /// Returns the busy share of the CPU time between two readings, rounded to 3 decimals.
    /// </summary>
    public static decimal? ComputeCpuFraction(ulong previousBusyTime, ulong previousTotalTime, ulong currentBusyTime, ulong currentTotalTime)
    {
        if (currentTotalTime <= previousTotalTime ||
            previousBusyTime > previousTotalTime ||
            currentBusyTime > currentTotalTime)
        {
            return null;
        }

        var previousIdleTime = previousTotalTime - previousBusyTime;
        var currentIdleTime = currentTotalTime - currentBusyTime;
        if (currentIdleTime < previousIdleTime)
        {
            return null;
        }

        var totalDelta = currentTotalTime - previousTotalTime;
        var idleDelta = Math.Min(currentIdleTime - previousIdleTime, totalDelta);
        return Math.Round((decimal)(totalDelta - idleDelta) / totalDelta, 3);
    }

    /// <summary>
    /// Returns the share of the elapsed time, per processor, that a cumulative time grew by, clamped to 0..1 and rounded to 3 decimals.
    /// </summary>
    public static decimal? ComputeTimeFraction(TimeSpan previous, TimeSpan current, TimeSpan elapsed, int processorCount = 1)
    {
        if (elapsed <= TimeSpan.Zero || current < previous || processorCount <= 0)
        {
            return null;
        }

        var fraction = (decimal)(current - previous).Ticks / ((decimal)elapsed.Ticks * processorCount);
        return Math.Round(Math.Clamp(fraction, 0m, 1m), 3);
    }

    /// <summary>
    /// Returns how many kilobytes (1000 bytes) per second a cumulative byte count grew by, rounded to whole kB/s.
    /// </summary>
    public static decimal? ComputeKilobytesPerSecond(long previousBytes, long currentBytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || currentBytes < previousBytes)
        {
            return null;
        }

        return Math.Round((currentBytes - previousBytes) / 1000m / (decimal)elapsed.TotalSeconds, 0);
    }

    /// <summary>
    /// Adds the delta of a 32-bit tick counter that may wrap around to a 64-bit running total.
    /// </summary>
    public static ulong AccumulateTicks(ulong accumulated, uint previous, uint current)
    {
        return accumulated + unchecked(current - previous);
    }
}

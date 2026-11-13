using System.Diagnostics;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Owns the <see cref="Stopwatch"/> timestamp of the baseline a rate is computed against and decides when enough
/// time passed since it. The caller keeps the cumulative values of the baseline.
/// </summary>
internal struct RateWindow
{
    // Below the 1 s minimum polling interval so a timer that fires a few milliseconds early still passes.
    /// <summary>
    /// The shortest elapsed time a rate is computed over; shorter windows give noisy values.
    /// </summary>
    public static readonly TimeSpan MinimumElapsed = TimeSpan.FromMilliseconds(900);

    private long? _baselineTimestamp;

    public readonly bool HasBaseline => _baselineTimestamp.HasValue;

    /// <summary>
    /// Drops the baseline, so the next <see cref="TryAdvance"/> takes a new one.
    /// </summary>
    public void Reset()
    {
        _baselineTimestamp = null;
    }

    /// <summary>
    /// Returns true with the time since the baseline when at least <see cref="MinimumElapsed"/> passed, and moves
    /// the baseline to <paramref name="timestamp"/>. Returns false and takes <paramref name="timestamp"/> as the
    /// baseline when there is none, and returns false and keeps the baseline when less time passed.
    /// </summary>
    public bool TryAdvance(long timestamp, out TimeSpan elapsed)
    {
        if (_baselineTimestamp is not { } baselineTimestamp)
        {
            _baselineTimestamp = timestamp;
            elapsed = TimeSpan.Zero;
            return false;
        }

        elapsed = Stopwatch.GetElapsedTime(baselineTimestamp, timestamp);
        if (elapsed < MinimumElapsed)
        {
            return false;
        }

        _baselineTimestamp = timestamp;
        return true;
    }

    /// <summary>
    /// Returns the time from <paramref name="timestamp"/> until <see cref="MinimumElapsed"/> passed since the
    /// baseline, or zero without a baseline or once it passed.
    /// </summary>
    public readonly TimeSpan Remaining(long timestamp)
    {
        if (_baselineTimestamp is not { } baselineTimestamp)
        {
            return TimeSpan.Zero;
        }

        var remaining = MinimumElapsed - Stopwatch.GetElapsedTime(baselineTimestamp, timestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

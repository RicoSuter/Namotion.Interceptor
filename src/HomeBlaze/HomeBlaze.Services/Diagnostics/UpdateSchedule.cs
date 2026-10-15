namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Decides when the next update check runs. The backoff is only touched by the update loop; the manual throttle may
/// be called from any thread.
/// </summary>
internal sealed class UpdateSchedule
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(365);
    public static readonly TimeSpan FirstCheckMinimumDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstCheckMaximumDelay = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(24);
    public static readonly TimeSpan ManualCheckThrottle = TimeSpan.FromMinutes(1);

    private readonly Lock _manualCheckLock = new();
    private int _failureCount;
    private DateTimeOffset? _lastManualCheck;

    /// <summary>
    /// Returns a uniformly random delay from FirstCheckMinimumDelay (inclusive) to FirstCheckMaximumDelay (exclusive).
    /// </summary>
    /// <remarks>
    /// The randomness keeps a container stuck in a restart loop from calling GitHub on every start.
    /// </remarks>
    public static TimeSpan GetFirstCheckDelay(Random random) =>
        FirstCheckMinimumDelay + (FirstCheckMaximumDelay - FirstCheckMinimumDelay) * random.NextDouble();

    /// <summary>
    /// Resets the backoff and returns the configured interval clamped to MinimumInterval..MaximumInterval.
    /// </summary>
    public TimeSpan GetDelayAfterSuccess(TimeSpan configuredInterval)
    {
        _failureCount = 0;

        if (configuredInterval < MinimumInterval)
        {
            return MinimumInterval;
        }

        return configuredInterval > MaximumInterval ? MaximumInterval : configuredInterval;
    }

    /// <summary>
    /// Advances the backoff and returns it, or a longer retryAfter up to MaximumBackoff.
    /// </summary>
    public TimeSpan GetDelayAfterFailure(TimeSpan? retryAfter)
    {
        var backoff = InitialBackoff * Math.Pow(2, Math.Min(_failureCount, 5));
        if (backoff > MaximumBackoff)
        {
            backoff = MaximumBackoff;
        }

        _failureCount++;

        if (retryAfter is { } requested && requested > backoff)
        {
            // A clock far in the past on a device without a real-time clock would otherwise stall checks for months.
            return requested > MaximumBackoff ? MaximumBackoff : requested;
        }

        return backoff;
    }

    /// <summary>
    /// Returns whether a manual check may start, at most once per ManualCheckThrottle.
    /// </summary>
    public bool TryStartManualCheck(DateTimeOffset now)
    {
        lock (_manualCheckLock)
        {
            if (_lastManualCheck is { } last)
            {
                var elapsed = now - last;

                // After the wall clock jumped back, waiting for it to catch up could block manual checks for hours.
                if (elapsed >= TimeSpan.Zero && elapsed < ManualCheckThrottle)
                {
                    return false;
                }
            }

            _lastManualCheck = now;
            return true;
        }
    }
}

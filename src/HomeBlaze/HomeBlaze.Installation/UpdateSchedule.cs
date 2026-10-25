namespace HomeBlaze.Installation;

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

    // Configuration written without ApplyConfigurationAsync, such as through MCP, wakes no one.
    public static readonly TimeSpan ConfigurationRecheckInterval = TimeSpan.FromHours(1);

    private readonly Lock _manualCheckLock = new();
    private int _failureCount;
    private long? _lastManualCheckTimestamp;

    // Random so a container stuck in a restart loop does not call GitHub on every start.
    /// <summary>
    /// Returns a uniformly random delay from FirstCheckMinimumDelay (inclusive) to FirstCheckMaximumDelay (exclusive).
    /// </summary>
    public static TimeSpan GetFirstCheckDelay(Random random) =>
        FirstCheckMinimumDelay + (FirstCheckMaximumDelay - FirstCheckMinimumDelay) * random.NextDouble();

    /// <summary>
    /// Starts the backoff over after a successful check.
    /// </summary>
    public void ResetBackoff()
    {
        _failureCount = 0;
    }

    /// <summary>
    /// Returns the configured interval clamped to MinimumInterval..MaximumInterval.
    /// </summary>
    public static TimeSpan ClampInterval(TimeSpan configuredInterval)
    {
        if (configuredInterval < MinimumInterval)
        {
            return MinimumInterval;
        }

        return configuredInterval > MaximumInterval ? MaximumInterval : configuredInterval;
    }

    /// <summary>
    /// Returns how long the update loop waits: the time until the next check is due, rounded up to whole
    /// milliseconds and at most ConfigurationRecheckInterval, or ConfigurationRecheckInterval when no check is due.
    /// </summary>
    public static TimeSpan GetWaitDelay(TimeSpan? remainingUntilDue)
    {
        if (remainingUntilDue is not { } remaining || remaining >= ConfigurationRecheckInterval)
        {
            return ConfigurationRecheckInterval;
        }

        // Rounded up so a timer that fires a fraction of a millisecond early waits again instead of spinning.
        return remaining > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)) : TimeSpan.Zero;
    }

    // A clock far in the past on a device without a real-time clock would otherwise stall checks for months.
    /// <summary>
    /// Returns the wait GitHub asked for, at most MaximumBackoff.
    /// </summary>
    public static TimeSpan CapRetryAfter(TimeSpan retryAfter) =>
        retryAfter > MaximumBackoff ? MaximumBackoff : retryAfter;

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
            return CapRetryAfter(requested);
        }

        return backoff;
    }

    /// <summary>
    /// Returns whether a manual check may start, at most once per ManualCheckThrottle on the monotonic clock.
    /// </summary>
    public bool TryStartManualCheck(TimeProvider timeProvider)
    {
        lock (_manualCheckLock)
        {
            var now = timeProvider.GetTimestamp();
            if (_lastManualCheckTimestamp is { } last && timeProvider.GetElapsedTime(last, now) < ManualCheckThrottle)
            {
                return false;
            }

            _lastManualCheckTimestamp = now;
            return true;
        }
    }
}

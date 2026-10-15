namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Decides when the next update check runs. The backoff is only touched by the update loop; the manual throttle may
/// be called from any thread.
/// </summary>
internal sealed class UpdateSchedule
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan FirstCheckMinimumDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstCheckMaximumDelay = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(24);
    public static readonly TimeSpan ManualCheckThrottle = TimeSpan.FromMinutes(1);

    private readonly Lock _manualCheckLock = new();
    private int _failureCount;
    private DateTimeOffset? _lastManualCheck;

    /// <summary>
    /// A random delay, so a container in a restart loop does not call GitHub on every start.
    /// </summary>
    public static TimeSpan GetFirstCheckDelay(Random random) =>
        FirstCheckMinimumDelay + (FirstCheckMaximumDelay - FirstCheckMinimumDelay) * random.NextDouble();

    public TimeSpan GetDelayAfterSuccess(TimeSpan configuredInterval)
    {
        _failureCount = 0;
        return configuredInterval < MinimumInterval ? MinimumInterval : configuredInterval;
    }

    public TimeSpan GetDelayAfterFailure(TimeSpan? retryAfter)
    {
        var backoff = InitialBackoff * Math.Pow(2, Math.Min(_failureCount, 5));
        if (backoff > MaximumBackoff)
        {
            backoff = MaximumBackoff;
        }

        _failureCount++;
        return retryAfter is { } requested && requested > backoff ? requested : backoff;
    }

    public bool TryStartManualCheck(DateTimeOffset now)
    {
        lock (_manualCheckLock)
        {
            if (_lastManualCheck is { } last && now - last < ManualCheckThrottle)
            {
                return false;
            }

            _lastManualCheck = now;
            return true;
        }
    }
}

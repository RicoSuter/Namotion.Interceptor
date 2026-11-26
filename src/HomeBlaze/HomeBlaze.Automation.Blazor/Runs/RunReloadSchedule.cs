namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Decides when the page reads the recent runs again: at once when no read started within the interval, otherwise when the
/// interval since the last read passed. A request while a read is due joins it, so that runs in quick succession cause one
/// read per interval. A retry, while history lags behind the live run counts, is scheduled only within the retry window after
/// the last change of the runs, so that a store that never stores a count stops the reads.
/// </summary>
internal sealed class RunReloadSchedule(TimeSpan interval, TimeSpan retryWindow)
{
    private DateTimeOffset? _lastStartedAt;
    private DateTimeOffset? _lastChangedAt;
    private bool _isDue;

    /// <summary>
    /// Records that the runs changed now and gets the delay after which the read is due, or null when a read is due already
    /// and takes this request.
    /// </summary>
    public TimeSpan? Request(DateTimeOffset now)
    {
        _lastChangedAt = now;
        return Schedule(now);
    }

    /// <summary>
    /// Gets the delay after which the read is due again because history lags behind, or null when a read is due already or
    /// the retry window since the last change has passed.
    /// </summary>
    public TimeSpan? Retry(DateTimeOffset now) =>
        _lastChangedAt is { } changed && now - changed < retryWindow ? Schedule(now) : null;

    /// <summary>Records that the due read starts now.</summary>
    public void Start(DateTimeOffset now)
    {
        _lastStartedAt = now;
        _isDue = false;
    }

    private TimeSpan? Schedule(DateTimeOffset now)
    {
        if (_isDue)
        {
            return null;
        }

        _isDue = true;
        return _lastStartedAt is { } last && now - last < interval ? last + interval - now : TimeSpan.Zero;
    }
}

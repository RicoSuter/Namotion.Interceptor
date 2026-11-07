namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Decides when the page reads the recent runs again: at once when no read started within the interval, otherwise when the
/// interval since the last read passed. A request while a read is due joins it, so that runs in quick succession cause one
/// read per interval.
/// </summary>
internal sealed class RunReloadSchedule(TimeSpan interval)
{
    private DateTimeOffset? _lastStartedAt;
    private bool _isDue;

    /// <summary>Gets the delay after which the read is due, or null when a read is due already and takes this request.</summary>
    public TimeSpan? Request(DateTimeOffset now)
    {
        if (_isDue)
        {
            return null;
        }

        _isDue = true;
        return _lastStartedAt is { } last && now - last < interval ? last + interval - now : TimeSpan.Zero;
    }

    /// <summary>Records that the due read starts now.</summary>
    public void Start(DateTimeOffset now)
    {
        _lastStartedAt = now;
        _isDue = false;
    }
}

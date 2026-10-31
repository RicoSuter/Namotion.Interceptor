namespace HomeBlaze.Game.World;

/// <summary>
/// A token bucket that refills one token per interval up to a burst, so network jitter does not refuse what a client
/// sent in time. Not thread-safe: its owner guards it with a lock.
/// </summary>
internal sealed class RateLimiter(TimeProvider timeProvider, TimeSpan interval, int burst)
{
    private double _tokens = burst;
    private long _lastTimestamp = timeProvider.GetTimestamp();

    public bool TryTake()
    {
        var now = timeProvider.GetTimestamp();
        var elapsed = timeProvider.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        _tokens = Math.Min(burst, _tokens + elapsed / interval);
        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}

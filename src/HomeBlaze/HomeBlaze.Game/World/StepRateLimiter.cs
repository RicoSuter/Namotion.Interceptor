namespace HomeBlaze.Game.World;

/// <summary>
/// A token bucket that refills one step per <see cref="Game.StepInterval"/> up to <see cref="Burst"/>, so network jitter
/// does not refuse steps a client sent in time. Not thread-safe: the game guards it with its lock.
/// </summary>
internal sealed class StepRateLimiter(TimeProvider timeProvider)
{
    public const int Burst = 3;

    private double _tokens = Burst;
    private long _lastTimestamp = timeProvider.GetTimestamp();

    public bool TryTake()
    {
        var now = timeProvider.GetTimestamp();
        var elapsed = timeProvider.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
        _tokens = Math.Min(Burst, _tokens + elapsed / Game.StepInterval);
        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}

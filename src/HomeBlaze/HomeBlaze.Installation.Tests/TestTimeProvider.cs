namespace HomeBlaze.Installation.Tests;

/// <summary>
/// A clock the test moves: <see cref="UtcNow"/> is the wall clock, which may also jump, and <see cref="Advance"/>
/// moves both it and the monotonic timestamp. Timers are not faked.
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private long _utcTicks = now.UtcTicks;
    private long _timestamp;

    public DateTimeOffset UtcNow
    {
        get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance(TimeSpan duration)
    {
        Interlocked.Add(ref _utcTicks, duration.Ticks);
        Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}

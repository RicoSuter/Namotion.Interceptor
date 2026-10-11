namespace HomeBlaze.AI.Tests;

/// <summary>A clock that only moves when a test advances it.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public void Advance(TimeSpan time) => _now += time;

    public override DateTimeOffset GetUtcNow() => _now;
}

using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.ZWave.Tests.Testing;

/// <summary>
/// A fake clock that records every timer created on it, so that a test advances the clock only once the timer it
/// expects exists, instead of racing the code under test.
/// </summary>
internal sealed class TimerRecordingTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _time = new();
    private readonly DateTimeOffset _start;
    private readonly Lock _lock = new();
    private readonly List<RecordedTimer> _timers = [];

    public TimerRecordingTimeProvider()
    {
        _start = _time.GetUtcNow();
    }

    /// <summary>
    /// The time advanced since this provider was created.
    /// </summary>
    public TimeSpan Elapsed => _time.GetUtcNow() - _start;

    public IReadOnlyList<RecordedTimer> Timers
    {
        get
        {
            lock (_lock)
            {
                return _timers.ToArray();
            }
        }
    }

    public override long TimestampFrequency => _time.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

    public override long GetTimestamp() => _time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Recorded after the timer exists, so that advancing the clock once the record is seen always reaches it.
        var timer = _time.CreateTimer(callback, state, dueTime, period);
        lock (_lock)
        {
            _timers.Add(new RecordedTimer(Elapsed, dueTime));
        }

        return timer;
    }

    public void Advance(TimeSpan delta) => _time.Advance(delta);

    /// <summary>
    /// Waits until the <paramref name="occurrence"/>th timer (counted from 1) with the given due time was created.
    /// </summary>
    public async Task<RecordedTimer> WaitForTimerAsync(TimeSpan dueTime, int occurrence = 1)
    {
        await AsyncTestHelpers.WaitUntilAsync(
            () => Find(dueTime, occurrence) is not null,
            () => $"No timer #{occurrence} of {dueTime.TotalMilliseconds} ms; timers: {string.Join(", ", Timers)}");
        return Find(dueTime, occurrence)!.Value;
    }

    private RecordedTimer? Find(TimeSpan dueTime, int occurrence) =>
        Timers.Where(timer => timer.DueTime == dueTime).Skip(occurrence - 1).Cast<RecordedTimer?>().FirstOrDefault();
}

/// <summary>
/// A timer created on <see cref="TimerRecordingTimeProvider"/>: when it was started and after how long it fires.
/// </summary>
internal readonly record struct RecordedTimer(TimeSpan StartedAt, TimeSpan DueTime);

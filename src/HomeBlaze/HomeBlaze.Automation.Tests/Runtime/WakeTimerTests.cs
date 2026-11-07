using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Automation.Tests.Runtime;

public class WakeTimerTests
{
    [Fact]
    public void WhenNothingIsDueAndNothingIsScheduled_ThenTheTimerIsNotChanged()
    {
        // Arrange
        var time = new CountingTimeProvider(new FakeTimeProvider(AutomationTestHost.Start));
        using var timer = new WakeTimer(time, () => { });
        var now = time.GetUtcNow();

        // Act
        timer.Schedule(null, now);
        timer.Schedule(null, now);

        // Assert
        Assert.Equal(0, time.Changes);
    }

    [Fact]
    public void WhenAScheduledWakeUpIsNoLongerDue_ThenTheTimerIsStoppedOnce()
    {
        // Arrange
        var time = new CountingTimeProvider(new FakeTimeProvider(AutomationTestHost.Start));
        using var timer = new WakeTimer(time, () => { });
        var now = time.GetUtcNow();
        timer.Schedule(now.AddSeconds(30), now);

        // Act
        timer.Schedule(null, now);
        timer.Schedule(null, now);

        // Assert
        Assert.Equal(2, time.Changes);
    }

    // Counts the changes of the timers it creates.
    private sealed class CountingTimeProvider(FakeTimeProvider time) : TimeProvider
    {
        public int Changes { get; private set; }

        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new CountingTimer(this, time.CreateTimer(callback, state, dueTime, period));

        private sealed class CountingTimer(CountingTimeProvider owner, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Changes++;
                return timer.Change(dueTime, period);
            }

            public void Dispose() => timer.Dispose();

            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}

using System.Reactive.Concurrency;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class TestSchedulersTests
{
    [Fact]
    public void WhenWorkIsScheduled_ThenItRunsOnlyWhenTheTestPumpsIt()
    {
        // Arrange
        var scheduler = new ControllableScheduler();
        var ran = 0;

        // Act
        scheduler.Schedule(0, (_, _) => { ran++; return System.Reactive.Disposables.Disposable.Empty; });

        // Assert
        Assert.Equal(0, ran);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.True(scheduler.RunOne());
        Assert.Equal(1, ran);
        Assert.False(scheduler.RunOne());
    }

    [Fact]
    public void WhenScheduledWorkThrows_ThenTheRecordingSchedulerCapturesItInsteadOfLettingItEscape()
    {
        // Arrange
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);

        // Act
        scheduler.Schedule(0, (_, _) => throw new InvalidOperationException("boom"));
        inner.RunAll();

        // Assert
        var escaped = Assert.Single(scheduler.Escaped);
        Assert.IsType<InvalidOperationException>(escaped);
    }

    [Fact]
    public void WhenAWorkItemSchedulesASuccessor_ThenRunAllStopsAtItWhileRunUntilIdleFollowsIt()
    {
        // Arrange
        var scheduler = new ControllableScheduler();
        var ran = new List<string>();

        scheduler.Schedule(0, (successorScheduler, _) =>
        {
            ran.Add("first");
            successorScheduler.Schedule(0, (_, _) =>
            {
                ran.Add("second");
                return System.Reactive.Disposables.Disposable.Empty;
            });

            return System.Reactive.Disposables.Disposable.Empty;
        });

        // Act
        var runAllCount = scheduler.RunAll();

        // Assert
        Assert.Equal(1, runAllCount);
        Assert.Equal(new[] { "first" }, ran);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(2, scheduler.ScheduleCallCount);

        scheduler.RunUntilIdle();
        Assert.Equal(new[] { "first", "second" }, ran);
        Assert.Equal(0, scheduler.QueuedCount);
    }
}

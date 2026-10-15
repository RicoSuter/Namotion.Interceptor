using HomeBlaze.History.Blazor;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class HistoryRefreshThrottleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void WhenArmedManyTimesWithinAWindow_ThenTheCallbackRunsOnceAtItsEnd()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var calls = 0;
        using var throttle = new HistoryRefreshThrottle(timeProvider, () => calls++);

        // Act
        var first = throttle.Arm(Window);
        var others = Enumerable.Range(0, 10).Select(_ => throttle.Arm(Window)).ToArray();
        timeProvider.Advance(Window - TimeSpan.FromTicks(1));
        var callsBeforeTheEnd = calls;
        timeProvider.Advance(TimeSpan.FromTicks(1));

        // Assert
        Assert.True(first);
        Assert.All(others, armed => Assert.False(armed));
        Assert.Equal(0, callsBeforeTheEnd);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WhenTheCallbackRan_ThenTheNextArmStartsANewWindow()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var calls = 0;
        using var throttle = new HistoryRefreshThrottle(timeProvider, () => calls++);
        throttle.Arm(Window);
        timeProvider.Advance(Window);

        // Act
        var armedAgain = throttle.Arm(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.True(armedAgain);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WhenTheCallbackArmsAgain_ThenItStartsTheNextWindow()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var calls = 0;
        HistoryRefreshThrottle? throttle = null;
        throttle = new HistoryRefreshThrottle(timeProvider, () =>
        {
            calls++;
            throttle!.Arm(Window);
        });
        using var disposable = throttle;
        throttle.Arm(Window);

        // Act
        timeProvider.Advance(Window);
        var armedDuringTheWindow = throttle.Arm(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.False(armedDuringTheWindow);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WhenNeverArmed_ThenTheCallbackNeverRuns()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var calls = 0;
        using var throttle = new HistoryRefreshThrottle(timeProvider, () => calls++);

        // Act
        timeProvider.Advance(Window * 10);

        // Assert
        Assert.Equal(0, calls);
    }

    [Fact]
    public void WhenDisposedWhileArmed_ThenTheCallbackDoesNotRunAndArmingFails()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var calls = 0;
        var throttle = new HistoryRefreshThrottle(timeProvider, () => calls++);
        throttle.Arm(Window);

        // Act
        throttle.Dispose();
        timeProvider.Advance(Window);
        var armedAfterDispose = throttle.Arm(Window);
        timeProvider.Advance(Window);

        // Assert
        Assert.Equal(0, calls);
        Assert.False(armedAfterDispose);
    }

    [Theory]
    [InlineData(60, null, 60)]
    [InlineData(30, null, 60)]
    [InlineData(300, null, 300)]
    [InlineData(300, 60, 300)]
    [InlineData(60, 3600, 3600)]
    [InlineData(10, 30, 60)]
    public void WhenTheWindowIsComputed_ThenItIsTheLargerOfTheIntervalAndTheBucket(
        int refreshIntervalSeconds, int? bucketSeconds, int expectedSeconds)
    {
        // Arrange
        TimeSpan? bucket = bucketSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        // Act
        var window = HistoryRefreshThrottle.GetWindow(TimeSpan.FromSeconds(refreshIntervalSeconds), bucket);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), window);
    }
}

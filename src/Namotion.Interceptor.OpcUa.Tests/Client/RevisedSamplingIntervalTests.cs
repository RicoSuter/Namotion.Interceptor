using Namotion.Interceptor.OpcUa.Client.Connection;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// What the client does with the sampling interval a server revises a monitored item to. It arrives as
/// a raw double and is what a read-back's timer is armed from, on the write path, so a value no timer
/// can be armed for would throw there and report the whole write batch failed on every retry.
/// </summary>
public class RevisedSamplingIntervalTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void WhenTheServerRevisesToAValueThatIsNotAnInterval_ThenNothingIsScheduledFromIt(double revised)
    {
        // Act
        var interval = SubscriptionManager.ToRevisedSamplingInterval(revised);

        // Assert: not positive, which is what leaves the property untracked for read-backs.
        Assert.True(interval <= TimeSpan.Zero);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(365d * 24 * 60 * 60 * 1000)]
    public void WhenTheServerRevisesBeyondWhatATimerCanBeArmedFor_ThenTheIntervalIsClamped(double revised)
    {
        // Arrange: a Timer refuses a delay past roughly 49.7 days, and the SDK server can legitimately
        // revise to a year.
        var timerCeiling = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        // Act
        var interval = SubscriptionManager.ToRevisedSamplingInterval(revised);

        // Assert: with headroom, because the read-back buffer is added on top before the timer is armed.
        Assert.True(interval > TimeSpan.Zero);
        Assert.True(interval < timerCeiling, $"{interval} should be armable, the ceiling is {timerCeiling}.");
    }

    [Fact]
    public void WhenTheServerRevisesToAnOrdinaryInterval_ThenItIsKept()
    {
        // Act
        var interval = SubscriptionManager.ToRevisedSamplingInterval(500d);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(500), interval);
    }
}

using HomeBlaze.Game.World;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.World;

public class RateLimiterTests
{
    [Fact]
    public void WhenFourStepsComeAtOnce_ThenTheBurstAllowsThreeOfThem()
    {
        // Arrange
        var limiter = new RateLimiter(new FakeTimeProvider(), Game.StepInterval, Game.StepBurst);

        // Act
        var results = new[] { limiter.TryTake(), limiter.TryTake(), limiter.TryTake(), limiter.TryTake() };

        // Assert
        Assert.Equal(new[] { true, true, true, false }, results);
    }

    [Fact]
    public void WhenOneIntervalPasses_ThenOneMoreStepIsAllowed()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var limiter = new RateLimiter(timeProvider, Game.StepInterval, Game.StepBurst);
        limiter.TryTake();
        limiter.TryTake();
        limiter.TryTake();

        // Act
        timeProvider.Advance(Game.StepInterval);
        var first = limiter.TryTake();
        var second = limiter.TryTake();

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void WhenMoreTimePassesThanTheBurstHolds_ThenTheTokensStopAtTheBurst()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var limiter = new RateLimiter(timeProvider, TimeSpan.FromMilliseconds(10), burst: 1);
        limiter.TryTake();

        // Act
        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        var results = new[] { limiter.TryTake(), limiter.TryTake() };

        // Assert
        Assert.Equal(new[] { true, false }, results);
    }
}

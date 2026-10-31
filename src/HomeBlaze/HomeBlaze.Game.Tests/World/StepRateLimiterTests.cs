using HomeBlaze.Game.World;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.World;

public class StepRateLimiterTests
{
    [Fact]
    public void WhenFourStepsComeAtOnce_ThenTheBurstAllowsThreeOfThem()
    {
        // Arrange
        var limiter = new StepRateLimiter(new FakeTimeProvider());

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
        var limiter = new StepRateLimiter(timeProvider);
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
}

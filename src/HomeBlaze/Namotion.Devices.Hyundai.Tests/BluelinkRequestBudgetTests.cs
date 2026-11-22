using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkRequestBudgetTests
{
    private static readonly DateTimeOffset Evening = new(2026, 11, 22, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenLimitIsReached_ThenConsumeThrowsWithoutCounting()
    {
        // Arrange
        var budget = new BluelinkRequestBudget(new FakeTimeProvider(Evening), () => 2);
        budget.Consume();
        budget.Consume();

        // Act & Assert
        Assert.Throws<BluelinkRequestLimitException>(budget.Consume);
        Assert.Equal(2, budget.Update());
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void WhenLocalMidnightPasses_ThenCounterResetsAndReportsIt()
    {
        // Arrange
        var time = new FakeTimeProvider(Evening);
        var budget = new BluelinkRequestBudget(time, () => 10);
        var reported = new List<int>();
        budget.CountChanged += reported.Add;
        budget.Consume();

        // Act
        time.Advance(TimeSpan.FromHours(2));
        var count = budget.Update();

        // Assert
        Assert.Equal(0, count);
        Assert.Equal([1, 0], reported);
    }

    [Fact]
    public void WhenEvening_ThenTimeUntilResetEndsAtLocalMidnight()
    {
        // Arrange
        var budget = new BluelinkRequestBudget(new FakeTimeProvider(Evening), () => 10);

        // Act & Assert
        Assert.Equal(TimeSpan.FromHours(2), budget.TimeUntilReset);
    }
}

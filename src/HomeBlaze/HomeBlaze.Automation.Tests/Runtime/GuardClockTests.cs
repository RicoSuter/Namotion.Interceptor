using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class GuardClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static GuardClock? Create(string guard)
    {
        Assert.True(AutomationExpressionCheck.TryParseExpression(guard, allowsEvent: false, out var expression, out var error), error);
        return GuardClock.Create(expression);
    }

    [Theory]
    [InlineData("./LastTriggeredAt == null or now - ./LastTriggeredAt > 10m", "./LastTriggeredAt", 600)]
    [InlineData("not /Blinds/IsMoving and (./LastTriggeredAt == null or now - ./LastTriggeredAt >= 1h30m)", "./LastTriggeredAt", 5400)]
    [InlineData("now - /Weather/LastUpdated < 15m && /Weather/Gust > 8", "/Weather/LastUpdated", 900)]
    public void WhenTheGuardComparesElapsedTime_ThenItChangesOnlyAtTheDerivedInstant(string guard, string path, int seconds)
    {
        // Act
        var clock = Create(guard);

        // Assert
        Assert.NotNull(clock);
        Assert.False(clock.IsEverySecond);
        Assert.Equal(new GuardClock.ElapsedComparison(path, TimeSpan.FromSeconds(seconds)), Assert.Single(clock.Comparisons));
    }

    [Theory]
    [InlineData("time > 22h")]
    [InlineData("now - ./LastTriggeredAt > 10m + 5s")]
    [InlineData("(now - ./LastTriggeredAt) > 10m")]
    [InlineData("now > ../StateEnteredAt + 5m")]
    [InlineData("now - ./LastTriggeredAt > 10m or now == ../StateEnteredAt")]
    [InlineData("now - ./LastTriggeredAt > 600")]
    public void WhenTheGuardReadsTheClockOtherwise_ThenItIsEvaluatedEverySecond(string guard)
    {
        // Act
        var clock = Create(guard);

        // Assert
        Assert.NotNull(clock);
        Assert.True(clock.IsEverySecond);
    }

    [Fact]
    public void WhenTheGuardDoesNotReadTheClock_ThenItHasNoClock()
    {
        // Act
        var clock = Create("not /Blinds/IsMoving");

        // Assert
        Assert.Null(clock);
    }

    [Fact]
    public void WhenTheInstantsAreRead_ThenTheNextChangeIsTheEarliestOneAfterNow()
    {
        // Arrange
        var clock = Create("now - ./LastTriggeredAt > 10m and now - /Weather/LastUpdated > 15m and now - ../StateEnteredAt > 1m")!;
        var instants = new Dictionary<string, object?>
        {
            ["./LastTriggeredAt"] = Now.AddMinutes(-5),
            ["/Weather/LastUpdated"] = Now.AddMinutes(-14),
            ["../StateEnteredAt"] = Now.AddMinutes(-2)
        };

        // Act
        var isDerived = clock.TryGetNextChange(path => instants[path], Now, out var nextChange);

        // Assert
        Assert.True(isDerived);
        Assert.Equal(Now.AddMinutes(1).AddTicks(1), nextChange);
    }

    [Fact]
    public void WhenAnInstantIsMissingOrPast_ThenItHasNoNextChange()
    {
        // Arrange
        var clock = Create("./LastTriggeredAt == null or now - ./LastTriggeredAt > 10m")!;

        // Act
        var isDerivedWithoutValue = clock.TryGetNextChange(_ => null, Now, out var withoutValue);
        var isDerivedWhenPast = clock.TryGetNextChange(_ => Now.AddHours(-1), Now, out var whenPast);
        var isDerivedForANumber = clock.TryGetNextChange(_ => 5.0, Now, out _);

        // Assert
        Assert.True(isDerivedWithoutValue);
        Assert.Null(withoutValue);
        Assert.True(isDerivedWhenPast);
        Assert.Null(whenPast);
        Assert.False(isDerivedForANumber);
    }
}

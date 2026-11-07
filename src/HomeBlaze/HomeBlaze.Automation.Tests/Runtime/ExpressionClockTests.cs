using System.Globalization;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class ExpressionClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static ExpressionClock? Create(string guard)
    {
        Assert.True(AutomationExpressionCheck.TryParseExpression(guard, allowsEvent: false, out var expression, out var error), error);
        return ExpressionClock.Create(expression);
    }

    [Theory]
    [InlineData("./LastTriggeredAt == null or now - ./LastTriggeredAt > 10m", "./LastTriggeredAt", 600, 1)]
    [InlineData("not /Blinds/IsMoving and (./LastTriggeredAt == null or now - ./LastTriggeredAt >= 1h30m)", "./LastTriggeredAt", 5400, 0)]
    [InlineData("now - /Weather/LastUpdated < 15m && /Weather/Gust > 8", "/Weather/LastUpdated", 900, 0)]
    public void WhenTheGuardComparesElapsedTime_ThenItChangesOnlyAtTheDerivedInstant(string guard, string path, int seconds, int ticks)
    {
        // Act
        var clock = Create(guard);

        // Assert
        Assert.NotNull(clock);
        Assert.False(clock.IsEverySecond);
        Assert.Equal(new ExpressionClock.ElapsedComparison(path, TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(ticks)), Assert.Single(clock.Elapsed));
    }

    [Theory]
    [InlineData("time > 22h + 1m")]
    [InlineData("22h < time")]
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
        var isDerived = clock.TryGetNextChange(path => instants[path], Now, TimeZoneInfo.Utc, out var nextChange);

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
        var isDerivedWithoutValue = clock.TryGetNextChange(_ => null, Now, TimeZoneInfo.Utc, out var withoutValue);
        var isDerivedWhenPast = clock.TryGetNextChange(_ => Now.AddHours(-1), Now, TimeZoneInfo.Utc, out var whenPast);
        var isDerivedForANumber = clock.TryGetNextChange(_ => 5.0, Now, TimeZoneInfo.Utc, out _);

        // Assert
        Assert.True(isDerivedWithoutValue);
        Assert.Null(withoutValue);
        Assert.True(isDerivedWhenPast);
        Assert.Null(whenPast);
        Assert.False(isDerivedForANumber);
    }

    [Theory]
    [InlineData("time >= 8h30m and time < 21h30m", "2026-11-06T07:00:00Z", "2026-11-06T07:30:00Z")] // 08:00 in Zurich, until 08:30
    [InlineData("time >= 8h30m and time < 21h30m", "2026-11-06T12:00:00Z", "2026-11-06T20:30:00Z")] // 13:00, until 21:30
    [InlineData("time > 22h", "2026-11-06T21:00:00Z", "2026-11-06T21:00:00.0000001Z")] // 22:00, one tick later
    [InlineData("time > 22h", "2026-11-06T22:00:00Z", "2026-11-06T23:00:00Z")] // 23:00, until midnight
    [InlineData("time >= 8h30m", "2026-03-28T23:00:00Z", "2026-03-29T01:00:00Z")] // 00:00, until daylight saving time starts
    [InlineData("time >= 8h30m", "2026-03-29T01:00:00Z", "2026-03-29T06:30:00Z")] // 03:00 in summer time, until 08:30
    [InlineData("time >= 2h30m", "2026-10-25T00:00:00Z", "2026-10-25T00:30:00Z")] // 02:00 in summer time, until 02:30
    [InlineData("time >= 2h30m", "2026-10-25T00:30:00Z", "2026-10-25T01:00:00Z")] // 02:30 in summer time, until it ends at 03:00
    [InlineData("time >= 2h30m", "2026-10-25T01:00:00Z", "2026-10-25T01:30:00Z")] // 02:00 again, until 02:30 again
    public void WhenTheExpressionComparesTheTimeOfDay_ThenTheNextChangeIsItsNextLocalTimeMidnightOrOffsetChange(string expression, string now, string expected)
    {
        // Arrange
        var clock = Create(expression)!;

        // Act
        var isDerived = clock.TryGetNextChange(_ => null, DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), Zurich, out var nextChange);

        // Assert
        Assert.False(clock.IsEverySecond);
        Assert.True(isDerived);
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), nextChange);
    }
}

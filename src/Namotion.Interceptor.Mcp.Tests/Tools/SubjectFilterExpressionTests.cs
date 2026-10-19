using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SubjectFilterExpressionTests
{
    [Theory]
    [InlineData("Temperature > 20", true)]
    [InlineData("Temperature < 20", false)]
    [InlineData("Temperature == 21.5", true)]
    [InlineData("Temperature >= 21.5", true)]
    [InlineData("Temperature <= 21.4", false)]
    [InlineData("Temperature > -5", true)]
    [InlineData("Name == \"kitchen\"", true)]
    [InlineData("Name == 'Kitchen'", true)]
    [InlineData("Name == Kitchen", true)]
    [InlineData("Name != Kitchen", false)]
    [InlineData("Name < \"L\"", true)]
    [InlineData("Temperature >= 21.5 && Name == Kitchen", true)]
    [InlineData("Temperature > 30 || Name == Kitchen", true)]
    [InlineData("Temperature > 30 or Name == Kitchen", true)]
    [InlineData("Temperature > 30 AND Name == Kitchen", false)]
    [InlineData("Name == Kitchen || Temperature > 30 && Temperature > 100", true)]
    [InlineData("(Name == Kitchen || Temperature > 30) && Temperature > 100", false)]
    [InlineData("Temperature == \"21.5\"", false)]
    [InlineData("Missing == 1", false)]
    [InlineData("Missing != 1", false)]
    [InlineData("Device != null", false)]
    public void WhenRoomIsFiltered_ThenComparisonFollowsValueType(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        // Act
        var matches = Evaluate(where, room);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("IsOn == true", true)]
    [InlineData("IsOn != false", true)]
    [InlineData("IsOn == false", false)]
    [InlineData("IsOn > false", false)]
    [InlineData("IsOn == 1", false)]
    public void WhenBooleanIsFiltered_ThenOnlyEqualityMatches(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        // Act
        var matches = Evaluate(where, device);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("Day == monday", true)]
    [InlineData("Day == \"Monday\"", true)]
    [InlineData("Day != Monday", false)]
    [InlineData("Day < Tuesday", false)]
    [InlineData("OptionalDay == null", true)]
    [InlineData("OptionalDay != null", false)]
    [InlineData("OptionalDay == Monday", false)]
    [InlineData("OptionalDay != Monday", false)]
    [InlineData("StartsAt > \"2026-10-01\"", true)]
    [InlineData("StartsAt < '2026-01-01T00:00:00Z'", false)]
    [InlineData("StartsAt == '2026-10-01T14:00:00+02:00'", true)]
    [InlineData("StartsAt > 'not a date'", false)]
    [InlineData("CreatedAt == '2026-01-01T00:00:00Z'", true)]
    public void WhenEnumNullOrDateIsFiltered_ThenComparisonFollowsValueType(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var schedule = new TestSchedule(context)
        {
            Day = DayOfWeek.Monday,
            OptionalDay = null,
            StartsAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
        };

        // Act
        var matches = Evaluate(where, schedule);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("IntValue == 5", true)]
    [InlineData("IntValue < 6", true)]
    [InlineData("IntValue > 5", false)]
    [InlineData("LongValue == 5000000000", true)]
    [InlineData("LongValue < 5000000001", true)]
    [InlineData("LongValue > 5000000000", false)]
    [InlineData("FloatValue == 0.1", true)]
    [InlineData("FloatValue < 0.2", true)]
    [InlineData("FloatValue > 0.1", false)]
    public void WhenNumericClrTypesAreFiltered_ThenComparisonUsesTheirPrecision(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var numbers = new TestNumbers(context) { IntValue = 5, LongValue = 5_000_000_000, FloatValue = 0.1f };

        // Act
        var matches = Evaluate(where, numbers);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("DoubleValue < 0.2")]
    [InlineData("DoubleValue <= 0.2")]
    [InlineData("DoubleValue > 0.2")]
    [InlineData("DoubleValue >= 0.2")]
    [InlineData("DoubleValue == 0.2")]
    [InlineData("DoubleValue != 0.2")]
    [InlineData("FloatValue < 0.2")]
    [InlineData("FloatValue <= 0.2")]
    [InlineData("FloatValue > 0.2")]
    [InlineData("FloatValue >= 0.2")]
    [InlineData("FloatValue == 0.2")]
    [InlineData("FloatValue != 0.2")]
    public void WhenValueIsNaN_ThenNoNumberComparisonMatches(string where)
    {
        // Arrange
        var context = CreateContext();
        var numbers = new TestNumbers(context) { FloatValue = float.NaN, DoubleValue = double.NaN };

        // Act
        var matches = Evaluate(where, numbers);

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void WhenExpressionIsParsed_ThenExposesEachReferencedPropertyNameOnce()
    {
        // Act
        Assert.True(SubjectFilterExpression.TryParse(
            "IsOn == true && (Temperature > 1 || IsOn == false) || Name != null", out var expression, out _));

        // Assert
        Assert.Equal(["IsOn", "Temperature", "Name"], expression.PropertyNames);
    }

    [Fact]
    public void WhenExpressionIsLongerThanLimit_ThenReturnsLengthError()
    {
        // Arrange
        var where = "Name == " + new string('a', 1001 - "Name == ".Length);

        // Act
        var parsed = SubjectFilterExpression.TryParse(where, out var expression, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal("where: expression is longer than 1000 characters at position 1000", error);
    }

    [Fact]
    public void WhenParenthesesAreNestedBeyondLimit_ThenReturnsNestingError()
    {
        // Arrange
        var where = new string('(', 33) + "Temperature > 0" + new string(')', 33);

        // Act
        var parsed = SubjectFilterExpression.TryParse(where, out var expression, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal("where: expression is nested too deeply at position 32", error);
    }

    [Fact]
    public void WhenParenthesesAreNestedAtLimit_ThenExpressionParsesAndMatches()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        var where = new string('(', 32) + "Temperature > 0" + new string(')', 32);

        // Act
        var parsed = SubjectFilterExpression.TryParse(where, out var expression, out _);

        // Assert
        Assert.True(parsed);
        Assert.True(expression!.Matches(room.TryGetRegisteredSubject()!, DefaultPathProvider.Instance));
    }

    [Fact]
    public void WhenPathProviderHidesProperty_ThenFilterDoesNotMatchIt()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };

        // Act
        var matches = Evaluate("Temperature > 0", room, new TemperatureHidingPathProvider());

        // Assert
        Assert.False(matches);
    }

    [Theory]
    [InlineData("", "where: expected a property name at position 0")]
    [InlineData("Temperature", "where: expected a comparison operator after 'Temperature' at position 11")]
    [InlineData("Temperature >", "where: expected a value after '>' at position 13")]
    [InlineData("Temperature = 1", "where: use '==' for equality at position 12")]
    [InlineData("(Temperature > 1", "where: expected ')' at position 16")]
    [InlineData("Name == \"abc", "where: unterminated string at position 8")]
    [InlineData("Temperature > 1 Name == x", "where: unexpected 'N' at position 16")]
    [InlineData("Temperature > 1.2.3", "where: invalid number '1.2.3' at position 14")]
    public void WhenExpressionIsInvalid_ThenReturnsErrorWithPosition(string where, string expectedError)
    {
        // Act
        var parsed = SubjectFilterExpression.TryParse(where, out var expression, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static bool Evaluate(string where, IInterceptorSubject subject, PathProviderBase? pathProvider = null)
    {
        Assert.True(SubjectFilterExpression.TryParse(where, out var expression, out _));
        return expression.Matches(subject.TryGetRegisteredSubject()!, pathProvider ?? DefaultPathProvider.Instance);
    }

    private sealed class TemperatureHidingPathProvider : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != "Temperature";

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
            IsPropertyIncluded(property) ? property.Name : null;
    }
}

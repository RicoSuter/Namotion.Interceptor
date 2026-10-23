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
    [InlineData("Device != null", true)]
    [InlineData("Device == null", false)]
    [InlineData("Device == Light", false)]
    [InlineData("Device", false)]
    [InlineData("not Device", true)]
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
    public void WhenExpressionIsParsed_ThenExposesEachPathOnce()
    {
        // Act
        Assert.True(SubjectFilterExpression.TryParse(
            "IsOn == true && (Temperature > 1 || IsOn == false) || Name != null",
            DefaultPathProvider.Instance.PathSeparator, out var expression, out _));

        // Assert
        Assert.Equal(["IsOn", "Temperature", "Name"], expression.Paths);
    }

    [Fact]
    public void WhenExpressionIsLongerThanLimit_ThenReturnsLengthError()
    {
        // Arrange
        var where = "Name == " + new string('a', 1001 - "Name == ".Length);

        // Act
        var parsed = SubjectFilterExpression.TryParse(where, DefaultPathProvider.Instance.PathSeparator, out var expression, out var error);

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
        var parsed = SubjectFilterExpression.TryParse(where, DefaultPathProvider.Instance.PathSeparator, out var expression, out var error);

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
        var parsed = SubjectFilterExpression.TryParse(where, DefaultPathProvider.Instance.PathSeparator, out var expression, out _);

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
        var matches = Evaluate("Temperature > 0", room, new HidingPathProvider("Temperature"));

        // Assert
        Assert.False(matches);
    }

    [Theory]
    [InlineData("", "where: expected a value at position 0")]
    [InlineData("Temperature >", "where: expected a value after '>' at position 13")]
    [InlineData("Temperature = 1", "where: use '==' for equality at position 12")]
    [InlineData("(Temperature > 1", "where: expected ')' at position 16")]
    [InlineData("Name == \"abc", "where: unterminated string at position 8")]
    [InlineData("Temperature > 1 Name == x", "where: unexpected 'N' at position 16")]
    [InlineData("Temperature > 1.2.3", "where: invalid number '1.2.3' at position 14")]
    public void WhenExpressionIsInvalid_ThenReturnsErrorWithPosition(string where, string expectedError)
    {
        // Act
        var parsed = SubjectFilterExpression.TryParse(where, DefaultPathProvider.Instance.PathSeparator, out var expression, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("Missing == null", true)]
    [InlineData("Missing != null", false)]
    [InlineData("Device.IsOn == true", true)]
    [InlineData("Device.DeviceName == Light", true)]
    [InlineData("Device.Missing == null", true)]
    [InlineData("not (Temperature > 30)", true)]
    [InlineData("!(Name == Kitchen)", false)]
    [InlineData("Device.IsOn", true)]
    [InlineData("not Device.IsOn", false)]
    [InlineData("subject.Name == Kitchen", true)]
    [InlineData("subject.Device.IsOn and Temperature > 20", true)]
    [InlineData("Temperature > Device.Missing", false)]
    [InlineData("not (Missing > 1)", false)]
    [InlineData("not (Device == Light)", false)]
    public void WhenFilterUsesPathsNotOrMissingValues_ThenItMatchesAsTheExpressionLanguageSays(string where, bool expected)
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
    [InlineData("value > 1", "where: 'value' is not available in where")]
    [InlineData("Name == value", "where: 'value' is not available in where")]
    [InlineData("Name | raw", "where: a condition takes no pipes")]
    [InlineData("Temperature - 1 > 0", "where: arithmetic is not supported at position 12")]
    public void WhenFilterUsesWhatWhereDoesNotHave_ThenReturnsError(string where, string expectedError)
    {
        // Act
        var parsed = SubjectFilterExpression.TryParse(where, '.', out var expression, out var error);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("Device == null", true)]
    [InlineData("Device != null", false)]
    [InlineData("Device.IsOn == null", true)]
    public void WhenPropertyHoldingASubjectIsNotSet_ThenItIsNull(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };

        // Act
        var matches = Evaluate(where, room);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("Devices != null", true)]
    [InlineData("Devices == null", false)]
    [InlineData("Devices[a].IsOn == true", true)]
    [InlineData("Devices[a] == null", true)]
    [InlineData("Devices[a] != null", false)]
    public void WhenPathHasAnIndex_ThenOnlyTheLastSegmentIsMissing(string where, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var group = new TestDeviceGroup(context);
        group.Devices = new Dictionary<string, TestDevice> { ["a"] = new TestDevice(context) { DeviceName = "Light", IsOn = true } };

        // Act
        var matches = Evaluate(where, group);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void WhenPathProviderHidesAnIntermediatePropertyOfThePath_ThenFilterDoesNotMatchIt()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        // Act
        var matches = Evaluate("Device.IsOn == true", room, new HidingPathProvider("Device"));

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void WhenPathProviderHidesAPropertyOnThePath_ThenFilterDoesNotMatchIt()
    {
        // Arrange
        var context = CreateContext();
        var room = new TestRoom(context) { Name = "Kitchen", Temperature = 21.5m };

        // Act
        var matches = Evaluate("subject.Temperature > 0", room, new HidingPathProvider("Temperature"));

        // Assert
        Assert.False(matches);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static bool Evaluate(string where, IInterceptorSubject subject, PathProviderBase? pathProvider = null)
    {
        pathProvider ??= DefaultPathProvider.Instance;
        Assert.True(SubjectFilterExpression.TryParse(where, pathProvider.PathSeparator, out var expression, out _));
        return expression.Matches(subject.TryGetRegisteredSubject()!, pathProvider);
    }

    private sealed class HidingPathProvider(string hiddenName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != hiddenName;

        public override string? TryGetPropertySegment(RegisteredSubjectProperty property) =>
            IsPropertyIncluded(property) ? property.Name : null;
    }
}

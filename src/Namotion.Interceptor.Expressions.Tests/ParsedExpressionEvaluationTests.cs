using Namotion.Interceptor.Expressions;

namespace Namotion.Interceptor.Expressions.Tests;

public class ParsedExpressionEvaluationTests
{
    private static bool? EvaluateCondition(string text, Dictionary<string, object?> values)
    {
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        var context = new TestExpressionContext(values);
        return expression.EvaluateCondition(ref context);
    }

    // The rows a filter host relies on.
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
    [InlineData("IsOn == true", true)]
    [InlineData("IsOn != false", true)]
    [InlineData("IsOn == false", false)]
    [InlineData("IsOn > false", false)]
    [InlineData("IsOn == 1", false)]
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
    [InlineData("IntValue == 5", true)]
    [InlineData("IntValue < 6", true)]
    [InlineData("IntValue > 5", false)]
    [InlineData("LongValue == 5000000000", true)]
    [InlineData("LongValue < 5000000001", true)]
    [InlineData("LongValue > 5000000000", false)]
    [InlineData("FloatValue == 0.1", true)]
    [InlineData("FloatValue < 0.2", true)]
    [InlineData("FloatValue > 0.1", false)]
    public void WhenConditionComparesValues_ThenTheComparerRulesApply(string text, bool expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["Name"] = "Kitchen",
            ["Temperature"] = 21.5m,
            ["IsOn"] = true,
            ["Day"] = DayOfWeek.Monday,
            ["OptionalDay"] = null,
            ["StartsAt"] = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ["CreatedAt"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            ["IntValue"] = 5,
            ["LongValue"] = 5_000_000_000L,
            ["FloatValue"] = 0.1f
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result == true);
    }

    [Theory]
    [InlineData("DoubleValue < 0.2")]
    [InlineData("DoubleValue == 0.2")]
    [InlineData("DoubleValue != 0.2")]
    [InlineData("FloatValue >= 0.2")]
    [InlineData("FloatValue != 0.2")]
    [InlineData("0.2 != FloatValue")]
    public void WhenValueIsNaN_ThenComparisonIsUnknown(string text)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["FloatValue"] = float.NaN, ["DoubleValue"] = double.NaN };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("IsCharging", true)]
    [InlineData("not IsCharging", false)]
    [InlineData("Unknown", null)]
    [InlineData("not Unknown", null)]
    [InlineData("Power", false)]
    [InlineData("IsCharging and Unknown", null)]
    [InlineData("IsPluggedIn and not Unknown", null)]
    [InlineData("NotCharging and Unknown", false)]
    [InlineData("IsCharging or Unknown", true)]
    [InlineData("NotCharging or Unknown", null)]
    [InlineData("Unknown == 1", null)]
    [InlineData("Unknown == null", true)]
    [InlineData("Power != null", true)]
    [InlineData("Power < null", null)]
    [InlineData("Unknown and NotCharging", false)]
    [InlineData("Unknown and IsCharging", null)]
    [InlineData("Unknown or IsCharging", true)]
    [InlineData("Unknown or NotCharging", null)]
    [InlineData("Unknown and Unknown", null)]
    [InlineData("Unknown or Unknown", null)]
    [InlineData("not (Power > 1 and Unknown)", null)]
    public void WhenConditionIsEvaluated_ThenLogicHasThreeValues(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["IsCharging"] = true,
            ["IsPluggedIn"] = true,
            ["NotCharging"] = false,
            ["Unknown"] = null,
            ["Power"] = 7400m
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    // A path on the right needs a slash; a bare word there is text, such as the name of an enum value.
    [Theory]
    [InlineData("meter/Power > meter/Maximum", false)]
    [InlineData("0.1 == meter/Float", true)]
    [InlineData("meter/Maximum >= meter/Power", true)]
    [InlineData("meter/Power > Maximum", null)]
    public void WhenBothOperandsAreValues_ThenTheyAreCompared(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["meter/Power"] = 7400m, ["meter/Maximum"] = 11000, ["meter/Float"] = 0.1f };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("'2026-10-01' < room/StartsAt", true)]
    [InlineData("'Monday' == room/Day", true)]
    [InlineData("'Monday' < room/Day", null)]
    [InlineData("'Kitchen' == room/Name", true)]
    public void WhenOperandsAreSwapped_ThenTheComparisonIsTheSame(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["room/Name"] = "Kitchen",
            ["room/Day"] = DayOfWeek.Monday,
            ["room/StartsAt"] = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenExpressionHasPipes_ThenTheyApplyInOrderToTheValue()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse("Name | upper:a | upper:b", '/', out var expression, out _));
        var context = new TestExpressionContext(new Dictionary<string, object?> { ["Name"] = "kitchen" });

        // Act
        var result = expression.Evaluate(ref context);
        var withoutPipes = expression.EvaluateWithoutPipes(ref context);

        // Assert
        Assert.True(result.IsText);
        Assert.Equal("KITCHENAb", result.Value);
        Assert.Equal("kitchen", withoutPipes.Value);
    }

    [Fact]
    public void WhenComparisonIsEvaluatedAsValue_ThenItIsABooleanOrMissing()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse("Power > 1000", '/', out var expression, out _));
        var known = new TestExpressionContext(new Dictionary<string, object?> { ["Power"] = 7400m });
        var unknown = new TestExpressionContext(new Dictionary<string, object?>());

        // Act
        var knownResult = expression.Evaluate(ref known);
        var unknownResult = expression.Evaluate(ref unknown);

        // Assert
        Assert.True(knownResult.Value is true);
        Assert.True(unknownResult.IsMissing);
    }

    [Fact]
    public void WhenConditionIsEvaluatedAgain_ThenItAllocatesNothing()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(
            "IsPluggedIn and not IsCharging and Power > 1000 and Day == Monday", '/', out var expression, out _));
        var context = new TestExpressionContext(new Dictionary<string, object?>
        {
            ["IsPluggedIn"] = true,
            ["IsCharging"] = false,
            ["Power"] = 7400m,
            ["Day"] = DayOfWeek.Monday
        });
        expression.EvaluateCondition(ref context);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            expression.EvaluateCondition(ref context);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }
}

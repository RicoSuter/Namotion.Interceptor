using System.Globalization;
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

    [Theory]
    [InlineData("room/First < room/Second", true)]
    [InlineData("room/Second < room/First", false)]
    [InlineData("room/First == room/First", true)]
    [InlineData("room/First >= room/Second", false)]
    public void WhenBothValuesAreOfTheSameEnum_ThenTheyAreOrderedByTheirValues(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["room/First"] = DayOfWeek.Monday, ["room/Second"] = DayOfWeek.Friday };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenValuesAreOfDifferentEnums_ThenTheyHaveNoOrder()
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["room/Day"] = DayOfWeek.Monday, ["room/Kind"] = DateTimeKind.Utc };

        // Act
        var result = EvaluateCondition("room/Day < room/Kind", values);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("room/Earlier < room/Later", true)]
    [InlineData("room/Later < room/Earlier", false)]
    [InlineData("room/Earlier == room/Same", true)]
    public void WhenBothValuesAreDateTimes_ThenTheyAreOrderedByTheirInstants(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["room/Earlier"] = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            ["room/Same"] = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Unspecified),
            ["room/Later"] = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("room/Earlier < room/Later", true)]
    [InlineData("room/Later < room/Earlier", false)]
    [InlineData("room/Earlier == room/Same", true)]
    public void WhenBothValuesAreDateTimeOffsets_ThenTheyAreOrderedByTheirInstants(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["room/Earlier"] = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ["room/Same"] = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2)),
            ["room/Later"] = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero)
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("room/Double == room/Float", true)]
    [InlineData("room/Float == room/Double", true)]
    [InlineData("room/Double <= room/Float", true)]
    [InlineData("room/Double > room/Float", false)]
    public void WhenADoubleIsComparedWithAFloat_ThenTheDoubleIsNarrowedToAFloat(string text, bool? expected)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["room/Double"] = 0.1, ["room/Float"] = 0.1f };

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

    [Theory]
    [InlineData("Interval == 90s", true)]
    [InlineData("Interval == 1m30s", true)]
    [InlineData("Interval == 0.025h", true)]
    [InlineData("Interval == 90000ms", true)]
    [InlineData("Interval > 1m", true)]
    [InlineData("Interval < 1.5m", false)]
    [InlineData("Interval != -90s", true)]
    [InlineData("Interval >= 1d", false)]
    public void WhenDurationLiteralIsCompared_ThenItComparesAsATimeSpan(string text, bool expected)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Interval"] = TimeSpan.FromSeconds(90) };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenDurationHasEveryUnit_ThenTheyAddUp()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse("1d2h3m4s5ms", '/', out var expression, out var error), error);
        var context = new TestExpressionContext([]);

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.Equal(new TimeSpan(1, 2, 3, 4, 5), result.Value);
    }

    [Theory]
    [InlineData("10675199d2h48m5.4775807s", long.MaxValue)]
    [InlineData("-10675199d2h48m5.4775808s", long.MinValue)]
    [InlineData("10675199d2h48m5s", long.MaxValue - 4775807)]
    [InlineData("0.0000001s", 1L)]
    public void WhenDurationIsAtTheEdgeOfTheRange_ThenItIsExactToTheTick(string text, long expectedTicks)
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        var context = new TestExpressionContext([]);

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.Equal(TimeSpan.FromTicks(expectedTicks), result.Value);
    }

    [Theory]
    [InlineData("Interval == 90")]
    [InlineData("Interval > Name")]
    public void WhenDurationIsComparedWithAnotherType_ThenTheComparisonIsUnknown(string text)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Interval"] = TimeSpan.FromSeconds(90), ["Name"] = "Kitchen" };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Power - 1000 > 6000", true)]
    [InlineData("Power + Offset == 7500", true)]
    [InlineData("Power * 2 == 14800", true)]
    [InlineData("Power / 2 == 3700", true)]
    [InlineData("Power - Offset * 2 == 7200", true)]
    [InlineData("(Power - Offset) * 2 == 14600", true)]
    [InlineData("Power - Offset - 100 == 7200", true)]
    [InlineData("Power / 2 / 2 == 1850", true)]
    [InlineData("Power - -100 == 7500", true)]
    [InlineData("Total > Phase1 + Phase2", true)]
    [InlineData("Total < Phase1 + Phase2", false)]
    [InlineData("Power - (Offset) == 7300", true)]
    [InlineData("Power * (Offset + 1) == 747400", true)]
    public void WhenOperandsAreNumbers_ThenArithmeticComputes(string text, bool expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["Power"] = 7400m,
            ["Offset"] = 100,
            ["Total"] = 10,
            ["Phase1"] = 3.0,
            ["Phase2"] = 4L
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Missing + 1 > 0")]
    [InlineData("1 + Missing > 0")]
    [InlineData("Power / 0 > 0")]
    [InlineData("Name + 1 > 0")]
    [InlineData("IsOn * 2 > 0")]
    public void WhenArithmeticHasNoNumber_ThenTheComparisonIsUnknown(string text)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Power"] = 7400m, ["Name"] = "Kitchen", ["IsOn"] = true };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Power - Offset", typeof(decimal), "7300")]
    [InlineData("Count + Count", typeof(decimal), "6")]
    [InlineData("Count / Four", typeof(decimal), "0.75")]
    [InlineData("Big + One", typeof(decimal), "9007199254740994")]
    [InlineData("Price + Price", typeof(decimal), "0.2")]
    [InlineData("Power - 1000", typeof(decimal), "6400")]
    [InlineData("Measured + 0.2", typeof(double), "0.30000000000000004")]
    [InlineData("Offset * 1.10", typeof(decimal), "110")]
    [InlineData("Amount * 2", typeof(decimal), "3")]
    [InlineData("Amount + 0.50", typeof(decimal), "2")]
    [InlineData("Amount / 0.5", typeof(decimal), "3")]
    [InlineData("One / Count", typeof(double), "0.3333333333333333")]
    [InlineData("Max + Max", typeof(double), "1.5845632502852868E+29")]
    [InlineData("Tiny * Tiny", typeof(double), "1E-40")]
    [InlineData("Ratio + 0", typeof(float), "0.1")]
    [InlineData("Ratio * Count", typeof(float), "0.3")]
    [InlineData("Ratio + Price", typeof(float), "0.2")]
    [InlineData("Zero * -1", typeof(double), "0")]
    public void WhenArithmeticIsEvaluatedAsValue_ThenItKeepsTheExactnessOfItsOperands(string text, Type expectedType, string expectedValue)
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        var context = new TestExpressionContext(new Dictionary<string, object?>
        {
            ["Power"] = 7400m,
            ["Offset"] = 100,
            ["Count"] = 3,
            ["Four"] = (byte)4,
            ["One"] = 1L,
            ["Big"] = 9007199254740993L,
            ["Price"] = 0.1m,
            ["Max"] = decimal.MaxValue,
            ["Tiny"] = 1e-20m,
            ["Ratio"] = 0.1f,
            ["Zero"] = 0d,
            ["Measured"] = 0.1,
            ["Amount"] = 1.50m
        });

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.IsType(expectedType, result.Value);
        Assert.Equal(expectedValue, Convert.ToString(result.Value, CultureInfo.InvariantCulture));
        Assert.Null(result.Property);
    }

    [Theory]
    [InlineData("-Power == -7400", true)]
    [InlineData("-(Power - Offset) == -7300", true)]
    [InlineData("Count * -Offset == -300", true)]
    [InlineData("Zero > -Count", true)]
    [InlineData("- -Power == 7400", true)]
    [InlineData("-Count * 2 == -6", true)]
    [InlineData("-Ratio == -0.1", true)]
    [InlineData("-Interval == -90s", true)]
    [InlineData("Interval > - 5m", true)]
    [InlineData("Now - EnteredAt > -Interval", true)]
    [InlineData("-Now == null", true)]
    [InlineData("-Name == null", true)]
    [InlineData("-IsOn == null", true)]
    [InlineData("-Missing == null", true)]
    [InlineData("-Smallest == null", true)]
    [InlineData("-NotANumber == null", true)]
    public void WhenOperandIsNegated_ThenNumbersAndDurationsChangeTheirSign(string text, bool expected)
    {
        // Arrange
        var values = CreateTimeValues();
        values["Power"] = 7400m;
        values["Offset"] = 100;
        values["Count"] = 3;
        values["Zero"] = 0d;
        values["Ratio"] = 0.1f;
        values["Name"] = "Kitchen";
        values["IsOn"] = true;
        values["Smallest"] = TimeSpan.MinValue;
        values["NotANumber"] = double.NaN;

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("-Count", typeof(decimal), "-3")]
    [InlineData("-Ratio", typeof(float), "-0.1")]
    [InlineData("-Zero", typeof(double), "0")]
    [InlineData("-Interval", typeof(TimeSpan), "-00:01:30")]
    [InlineData("-Amount", typeof(decimal), "-1.5")]
    public void WhenNegationIsEvaluatedAsValue_ThenItKeepsTheTypeOfItsOperand(string text, Type expectedType, string expectedValue)
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        var context = new TestExpressionContext(new Dictionary<string, object?>
        {
            ["Count"] = 3,
            ["Ratio"] = 0.1f,
            ["Zero"] = 0d,
            ["Interval"] = TimeSpan.FromSeconds(90),
            ["Amount"] = 1.50m
        });

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.IsType(expectedType, result.Value);
        Assert.Equal(expectedValue, Convert.ToString(result.Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("First + Second == 0.3", true)]
    [InlineData("Second - First == 0.1", true)]
    [InlineData("First * Count == 0.3", true)]
    [InlineData("Ratio + 0 == 0.1", true)]
    [InlineData("Ratio * 10 == 1", true)]
    [InlineData("Ratio - Ratio == 0", true)]
    [InlineData("First * 3 == 0.3", true)]
    [InlineData("0.1 + 0.2 == 0.3", true)]
    [InlineData("Ratio == 0.1", true)]
    [InlineData("Ratio < 0.1", false)]
    [InlineData("Ratio + 0.2 == 0.3", true)]
    [InlineData("Measured + 0.2 == 0.3", false)]
    [InlineData("Measured + 0.2 == 0.30000000000000004", true)]
    [InlineData("Measured == 0.1", true)]
    [InlineData("Big == 9007199254740993", true)]
    [InlineData("Big == 9007199254740992", false)]
    [InlineData("Big > 9007199254740992", true)]
    [InlineData("Count == 3", true)]
    [InlineData("Count == 3.0", true)]
    [InlineData("Count == 3e0", true)]
    public void WhenOperandsAreExactOrFloats_ThenTheResultEqualsTheLiteral(string text, bool expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["First"] = 0.1m,
            ["Second"] = 0.2m,
            ["Count"] = 3,
            ["Ratio"] = 0.1f,
            ["Measured"] = 0.1,
            ["Big"] = 9007199254740993L
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.50", typeof(decimal), "1.5")]
    [InlineData("11.000", typeof(decimal), "11")]
    [InlineData("0.00", typeof(decimal), "0")]
    [InlineData("-5", typeof(decimal), "-5")]
    [InlineData(".5", typeof(decimal), "0.5")]
    [InlineData("0.1 + 0.2", typeof(decimal), "0.3")]
    [InlineData("1e3", typeof(double), "1000")]
    [InlineData("1000000000000000000000000000000", typeof(double), "1E+30")]
    [InlineData("0.12345678901234567890123456789", typeof(double), "0.12345678901234568")]
    public void WhenNumberLiteralIsEvaluated_ThenItIsADecimalUnlessItHasAnExponentOrDoesNotFit(string text, Type expectedType, string expectedValue)
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        var context = new TestExpressionContext([]);

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.IsType(expectedType, result.Value);
        Assert.Equal(expectedValue, Convert.ToString(result.Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Now - EnteredAt > 30m", true)]
    [InlineData("Now - EnteredAt == 38m", true)]
    [InlineData("Now - EnteredAt < 1h", true)]
    [InlineData("Now == EnteredAt + 38m", true)]
    [InlineData("Now == 38m + EnteredAt", true)]
    [InlineData("EnteredAt == Now - 38m", true)]
    [InlineData("EnteredAt + 10m == '2026-11-06T14:12:00Z'", true)]
    [InlineData("Now - Created == 40m", true)]
    [InlineData("Interval + 30s == 2m", true)]
    [InlineData("Interval - 30s == 1m", true)]
    [InlineData("Now - EnteredAt - Interval == 36m30s", true)]
    public void WhenOperandsAreInstantsOrDurations_ThenArithmeticGivesInstantsAndDurations(string text, bool expected)
    {
        // Arrange
        var values = CreateTimeValues();

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Now + EnteredAt > 0m")]
    [InlineData("Interval - EnteredAt > 0m")]
    [InlineData("Now - 5 > 0m")]
    [InlineData("Interval * 2 > 1m")]
    [InlineData("Interval / 2 > 1m")]
    [InlineData("EnteredAt + 3000000d > Now")]
    public void WhenTimeArithmeticHasNoMeaning_ThenTheComparisonIsUnknown(string text)
    {
        // Arrange
        var values = CreateTimeValues();

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenInstantIsSubtractedFromInstant_ThenTheValueIsATimeSpan()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse("Now - EnteredAt", '/', out var expression, out var error), error);
        var context = new TestExpressionContext(CreateTimeValues());

        // Act
        var result = expression.Evaluate(ref context);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(38), result.Value);
    }

    [Theory]
    [InlineData("Meter/Local + 1h > Meter/Local", true)]
    [InlineData("Meter/Local - 1h < Meter/Local", true)]
    [InlineData("Meter/Offset == Meter/Local", true)]
    [InlineData("Meter/Local == Meter/Offset", true)]
    [InlineData("Meter/Offset != Meter/Local", false)]
    [InlineData("Meter/Offset < Meter/Local", false)]
    [InlineData("Meter/Local >= Meter/Offset", true)]
    public void WhenDateTimeAndDateTimeOffsetAreCompared_ThenTheyCompareByInstant(string text, bool expected)
    {
        // Arrange
        var values = new Dictionary<string, object?>
        {
            ["Meter/Offset"] = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.FromHours(1)),
            ["Meter/Local"] = new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var result = EvaluateCondition(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    // Which edge leaves the range depends on the offset of the local time zone; in UTC both stay in it.
    [Theory]
    [InlineData("Clock/Edge < Clock/Offset")]
    [InlineData("Clock/Offset > Clock/Edge")]
    [InlineData("Clock/Edge == Clock/Utc")]
    [InlineData("Clock/Edge < '2026-01-01'")]
    public void WhenLocalDateTimeHasNoInstantInRange_ThenTheComparisonIsUnknown(string text)
    {
        foreach (var edge in new[] { DateTime.MinValue, DateTime.MaxValue })
        {
            // Arrange
            var local = DateTime.SpecifyKind(edge, DateTimeKind.Local);
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            var isOutOfRange = edge == DateTime.MinValue ? offset > TimeSpan.Zero : offset < TimeSpan.Zero;
            var values = new Dictionary<string, object?>
            {
                ["Clock/Edge"] = local,
                ["Clock/Offset"] = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero),
                ["Clock/Utc"] = new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc)
            };

            // Act
            var result = EvaluateCondition(text, values);

            // Assert
            Assert.Equal(isOutOfRange, result is null);
        }
    }

    [Fact]
    public void WhenDurationOverflows_ThenTheComparisonIsUnknown()
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Interval"] = TimeSpan.FromDays(10675198) };

        // Act
        var result = EvaluateCondition("Interval + 10675198d > 0m", values);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenNumberIsDividedByZero_ThenTheResultIsMissing()
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Power"] = 7400m };

        // Act
        var result = EvaluateCondition("Power / 0 == null", values);

        // Assert
        Assert.True(result);
    }

    private static Dictionary<string, object?> CreateTimeValues() => new()
    {
        ["Now"] = new DateTimeOffset(2026, 11, 6, 14, 40, 0, TimeSpan.Zero),
        ["EnteredAt"] = new DateTimeOffset(2026, 11, 6, 15, 2, 0, TimeSpan.FromHours(1)),
        ["Created"] = new DateTime(2026, 11, 6, 14, 0, 0, DateTimeKind.Unspecified),
        ["Interval"] = TimeSpan.FromSeconds(90)
    };
}

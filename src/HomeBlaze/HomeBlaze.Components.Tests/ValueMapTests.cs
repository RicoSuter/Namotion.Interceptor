using HomeBlaze.Services;
using HomeBlaze.Services.Display;

namespace HomeBlaze.Components.Tests;

public class ValueMapTests
{
    // Conditions on the value need no graph; a path of a case leads nowhere here.
    private static readonly SubjectPathResolver Resolver = new(() => null);

    private static ValueMapCase? Find(ValueMap map, object? value) =>
        map.FindCase(new ValueBinding(null, value, null), Resolver);

    [Theory]
    [InlineData(6000, "high")]
    [InlineData(1500, "medium")]
    [InlineData(10, "low")]
    public void WhenCasesHaveConditionsOnTheValue_ThenTheFirstTrueOneMatches(int power, string expected)
    {
        // Arrange
        var map = new ValueMap
        {
            Cases =
            [
                new ValueMapCase { When = "value >= 5000", Text = "high" },
                new ValueMapCase { When = "value >= 1000", Text = "medium" },
                new ValueMapCase { Text = "low" }
            ]
        };

        // Act
        var matched = Find(map, (decimal)power);

        // Assert
        Assert.Equal(expected, matched?.Text);
    }

    [Theory]
    [InlineData("Charging", "slow")]
    [InlineData("Ready", null)]
    public void WhenCaseHasValueAndAnUnknownWhen_ThenOnlyTheCaseWithoutTheConditionMatches(string state, string? expected)
    {
        // Arrange
        var map = new ValueMap
        {
            Cases =
            [
                new ValueMapCase { Value = "Charging", When = "subject/ChargingPower > 5000", Text = "fast" },
                new ValueMapCase { Value = "charging", Text = "slow" }
            ]
        };

        // Act
        var matched = map.FindCase(new ValueBinding(null, state, null), Resolver);

        // Assert: without a subject, subject/ChargingPower is missing and the condition is unknown
        Assert.Equal(expected, matched?.Text);
    }

    [Theory]
    [InlineData("Charging", true, "fast")]
    [InlineData("Charging", false, "slow")]
    [InlineData("Ready", true, null)]
    public void WhenCaseHasValueAndWhen_ThenBothMustHold(string state, bool isFast, string? expected)
    {
        // Arrange: the condition reads the value only, so no graph is needed
        var map = new ValueMap
        {
            Cases =
            [
                new ValueMapCase { Value = "Charging", When = isFast ? "value == Charging" : "value == Fast", Text = "fast" },
                new ValueMapCase { Value = "charging", Text = "slow" }
            ]
        };

        // Act
        var matched = map.FindCase(new ValueBinding(null, state, null), Resolver);

        // Assert
        Assert.Equal(expected, matched?.Text);
    }

    [Fact]
    public void WhenValueIsMissing_ThenOnlyTheDefaultCaseMatches()
    {
        // Arrange
        var map = new ValueMap
        {
            Cases =
            [
                new ValueMapCase { Value = "True", Text = "on" },
                new ValueMapCase { When = "not value", Text = "off" },
                new ValueMapCase { Text = "unknown" }
            ]
        };

        // Act
        var matched = Find(map, null);

        // Assert
        Assert.Equal("unknown", matched?.Text);
    }

    [Fact]
    public void WhenCaseTextHasExpressions_ThenTheyShowTheValue()
    {
        // Arrange
        var map = new ValueMap { Cases = [new ValueMapCase { Text = "Power: {{ value }} ({{ value | raw }})" }] };
        var binding = new ValueBinding(null, 1234.567m, null);

        // Act
        var text = map.GetText(binding, Resolver, null);

        // Assert
        Assert.Equal("Power: 1234.57 (1234.567)", text);
    }

    [Fact]
    public void WhenCaseHasNoText_ThenTheMapGivesNoText()
    {
        // Arrange
        var map = new ValueMap { Cases = [new ValueMapCase { Value = "True", Color = "Success" }] };

        // Act
        var text = map.GetText(new ValueBinding(null, true, null), Resolver, null);

        // Assert
        Assert.Null(text);
    }

    [Theory]
    [InlineData("value >", null, "When: expected a value after '>' at position 7")]
    [InlineData("value | raw", null, "When: A condition takes no pipes")]
    [InlineData(null, "{{ value | rounded }}", "Text: Unknown pipe 'rounded'")]
    [InlineData(null, "{{ value | map:x }}", "Text: Unknown pipe 'map'")]
    [InlineData(null, "a {{ value", "Text: '{{' is not closed at position 2")]
    [InlineData("value > 1", "{{ subject/Power }} W", null)]
    public void WhenCaseHasAMistake_ThenItsErrorNamesTheArgument(string? when, string? text, string? expected)
    {
        // Arrange
        var valueCase = new ValueMapCase { When = when, Text = text };

        // Act
        var error = valueCase.GetExpressionError();

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void WhenConditionHasAMistake_ThenTheCaseMatchesNothing()
    {
        // Arrange
        var map = new ValueMap { Cases = [new ValueMapCase { When = "value >", Text = "broken" }, new ValueMapCase { Text = "other" }] };

        // Act
        var matched = Find(map, 5m);

        // Assert
        Assert.Equal("other", matched?.Text);
    }

    [Fact]
    public void WhenConditionIsEvaluatedAgain_ThenTheMatchAllocatesNothing()
    {
        // Arrange
        var map = new ValueMap
        {
            Cases = [new ValueMapCase { When = "value > 5000", Text = "high" }, new ValueMapCase { When = "value > 1000", Text = "medium" }]
        };
        var binding = new ValueBinding(null, true, null);
        map.FindCase(binding, Resolver);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            map.FindCase(binding, Resolver);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenNoCaseHasAValue_ThenTheMatchOfANumberAllocatesNothing()
    {
        // Arrange
        var map = new ValueMap
        {
            Cases = [new ValueMapCase { When = "value > 5000", Text = "high" }, new ValueMapCase { When = "value > 1000", Text = "medium" }]
        };
        var binding = new ValueBinding(null, 7400m, null);
        map.FindCase(binding, Resolver);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            map.FindCase(binding, Resolver);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }
}

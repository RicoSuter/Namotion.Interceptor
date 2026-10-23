using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;

namespace HomeBlaze.Services.Tests.Display;

public class ExpressionDisplayTests
{
    private static string? Check(string text, ExpressionUse use)
    {
        Assert.True(ParsedExpression.TryParse(text, out var expression, out var error), error);
        return ExpressionDisplay.Check(expression, use);
    }

    [Theory]
    [InlineData("a/B", ExpressionUse.PageText, null)]
    [InlineData("a/B | raw | DISPLAY", ExpressionUse.PageText, null)]
    [InlineData("a/B | rounded", ExpressionUse.PageText, "Unknown pipe 'rounded'")]
    [InlineData("a/B | raw:2", ExpressionUse.PageText, "The pipe 'raw' takes no arguments")]
    [InlineData("value", ExpressionUse.PageText, "'value' is not available in the text of a page")]
    [InlineData("subject/Power > 1", ExpressionUse.PageText, "'subject' is not available in the text of a page")]
    [InlineData("subject/Power | raw", ExpressionUse.CaseText, null)]
    [InlineData("a/B | map:onoff", ExpressionUse.PageText, null)]
    [InlineData("a/B > 1 | map:onoff | raw", ExpressionUse.PageText, null)]
    [InlineData("a/B | map", ExpressionUse.PageText, "The pipe 'map' takes the name of a value map, such as map:wallbox")]
    [InlineData("a/B | map:x:y", ExpressionUse.PageText, "The pipe 'map' takes the name of a value map, such as map:wallbox")]
    [InlineData("value > 1000 and subject/IsCharging", ExpressionUse.CaseCondition, null)]
    [InlineData("value | raw", ExpressionUse.CaseCondition, "A condition takes no pipes")]
    [InlineData("subject/ChargingPower | map:wallbox | raw", ExpressionUse.WidgetText, null)]
    [InlineData("value > 1000", ExpressionUse.WidgetText, null)]
    [InlineData("value | rounded", ExpressionUse.WidgetText, "Unknown pipe 'rounded'")]
    [InlineData("a/B | map", ExpressionUse.WidgetText, "The pipe 'map' takes the name of a value map, such as map:wallbox")]
    [InlineData("a/B | map:onoff", ExpressionUse.WidgetTextWithoutPath, null)]
    [InlineData("subject/Name", ExpressionUse.WidgetTextWithoutPath, "'subject' is not available in a widget without a Path")]
    [InlineData("a/B == value", ExpressionUse.WidgetTextWithoutPath, "'value' is not available in a widget without a Path")]
    [InlineData("a/B == value/X", ExpressionUse.WidgetTextWithoutPath, "'value' is not available in a widget without a Path")]
    [InlineData("a/B | raw | map:x", ExpressionUse.PageText, "The pipe 'map' must be the first pipe")]
    [InlineData("a/B | display | map:x", ExpressionUse.PageText, "The pipe 'map' must be the first pipe")]
    [InlineData("a/B | map:x | map:y", ExpressionUse.PageText, "The pipe 'map' must be the first pipe")]
    [InlineData("value | map:x", ExpressionUse.CaseText, "The pipe 'map' cannot be used in the text of a case")]
    [InlineData("value | raw | map:x", ExpressionUse.CaseText, "The pipe 'map' cannot be used in the text of a case")]
    [InlineData("value/IsCharging", ExpressionUse.CaseCondition, "'value' has no members: write subject/... to read a property of its subject")]
    [InlineData("subject/A and value[0] == 1", ExpressionUse.CaseCondition, "'value' has no members: write subject/... to read a property of its subject")]
    [InlineData("value/IsCharging | raw", ExpressionUse.CaseText, "'value' has no members: write subject/... to read a property of its subject")]
    [InlineData("subject/", ExpressionUse.CaseCondition, "expected a name after 'subject/'")]
    [InlineData("subject/ == 1", ExpressionUse.CaseCondition, "expected a name after 'subject/'")]
    [InlineData("subject/ | raw", ExpressionUse.CaseText, "expected a name after 'subject/'")]
    [InlineData("subject//X", ExpressionUse.CaseCondition, "a path starts at the subject and cannot start with '/'")]
    [InlineData("subject//X | raw", ExpressionUse.CaseText, "a path starts at the subject and cannot start with '/'")]
    [InlineData("subject/Power.Max > value", ExpressionUse.CaseCondition, null)]
    [InlineData("subject/Maximum < value", ExpressionUse.CaseCondition, null)]
    [InlineData("subject > 1 and value", ExpressionUse.CaseCondition, null)]
    public void WhenExpressionIsChecked_ThenItsPipesAndVariablesMustFitWhereItStands(string text, ExpressionUse use, string? expected)
    {
        // Act
        var error = Check(text, use);

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void WhenResultHasNoPropertyOrIsMissingOrText_ThenTextIsYesNoEmptyOrAsItIs()
    {
        // Act
        var yes = ExpressionDisplay.ToText(ExpressionValue.FromBoolean(true), null);
        var missing = ExpressionDisplay.ToText(ExpressionValue.Missing, null);
        var text = ExpressionDisplay.ToText(ExpressionValue.FromText("as is"), null);

        // Assert
        Assert.Equal("Yes", yes);
        Assert.Equal(string.Empty, missing);
        Assert.Equal("as is", text);
    }

    [Fact]
    public void WhenRawIsApplied_ThenTheValueIsWrittenInvariant()
    {
        // Act
        var raw = ExpressionDisplay.ApplyFormat("raw", ExpressionValue.FromValue(1234.5m), null);
        var display = ExpressionDisplay.ApplyFormat("display", ExpressionValue.FromValue(true), null);

        // Assert
        Assert.True(raw.IsText);
        Assert.Equal("1234.5", raw.Value);
        Assert.Equal("Yes", display.Value);
    }
}

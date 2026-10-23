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
    [InlineData("value | map:x", ExpressionUse.CaseText, "Unknown pipe 'map'")]
    [InlineData("value > 1000 and subject/IsCharging", ExpressionUse.CaseCondition, null)]
    [InlineData("value | raw", ExpressionUse.CaseCondition, "A condition takes no pipes")]
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
